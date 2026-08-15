using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Tests one falsifiable claim: flat (white) frames are the frames in which a
    /// guest-to-host upload (SetData) or a texture-to-texture copy landed on a
    /// scene-sized texture mid-frame - the shared layer resurrecting stale guest
    /// memory into a surface the frame had already rendered.
    ///
    /// Per frame it accumulates, in memory only: how many SetData/CopyTo writes hit
    /// large textures, whether the written texture had already been a colour
    /// attachment this same frame (the resurrection signature), and which shapes.
    /// At present it samples 25 pixels of the present source into a slot of a ring
    /// buffer - same grid and threshold FlashGuard's calibrated detector uses - and
    /// classifies the slot several presents later when its command buffer has long
    /// completed. Nothing is waited on, and nothing is logged per frame; a per-frame
    /// present log is known to close the race window, a per-frame GPU sync is known
    /// (from the CPU-sampling FlashGuard era, 35% flat with it active) not to.
    ///
    /// The join is a 2x2 table reported once per interval: P(big write | flat) vs
    /// P(big write | normal), plus the same split per shape. Hypothesis dead if the
    /// columns match; culprit named by shape if they do not.
    ///
    /// RYUJINX_METAL_UPLOAD_CORR=1 enables. No MSL is touched, so no CodeGenVersion
    /// bump is due.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class UploadCorrelator
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_UPLOAD_CORR") == "1";

        private const int GridSide = 5;
        private const int Pixels = GridSide * GridSide;
        private const int BytesPerPixel = 4;
        private const int Slots = 8;

        // FlashGuard's calibrated flat-frame criterion (300 captured frames of real
        // gameplay): a flat frame has >= 8 of 25 samples saturated, an ordinary one
        // at most 4. Same numbers so the labels mean the same thing they meant there.
        private const float SaturatedLuma = 235f;
        private const int SaturatedNeeded = 6;

        // Below this a texture is a shadow tile, LUT or UI atlas, not a stage of the
        // scene chain. The scene runs 1600x896 at full dynamic resolution and stays
        // far above this even scaled down; the 260x260 background-readback texture
        // stays below it.
        private const int MinPixels = 512 * 512;

        private struct Slot
        {
            public bool Valid;
            public FenceHolder Fence;
            public long Frame;
            public int Uploads;
            public int BigUploads;
            public int BigUploadsOntoRt;
            public int BigCopies;
            public int BigCopiesOntoRt;
            public ulong[] Shapes;
            public int ShapeCount;
            public Binding Binding;
            public Binding BindingLast;
            public string Signature;
            public IntPtr PresentRoot;
            public long PresentAge;
            public string Writers;
            public bool InputSampled;
            public bool InputWrittenAfter;
            public int Residency;
            public int CompositeDraws;
            public int VertexDistinct;
            public int VertexStride;
            public (int Count, int Inst, int First, int Indexed) Draw;
            public IntPtr ArgPtr;
            public ulong[] ArgExpected;
            public int ArgCount;
            public string LateWriter;
            public float[] Cb;
            public bool[] CbSeen;
        }

        private static MTLBuffer _buf;
        private static readonly Slot[] _slots = new Slot[Slots];

        // Accumulators for the frame currently being encoded. Everything here runs on
        // the render thread (SetData uses the main pipeline, attachments bind at
        // encoder creation, present is the boundary), so plain fields suffice.
        private static readonly HashSet<IntPtr> _attachedThisFrame = new();
        // The scene texture's binding as the shader receives it, for the last draw of
        // the frame that sampled it. Recorded per frame, classified with the frame.
        private struct Binding
        {
            public ulong GpuAddress;
            public IntPtr NativePtr;
            public IntPtr CanonicalPtr;
            public string Program;
            public int Count;
        }

        private static Binding _frameBinding;
        private static Binding _frameBindingLast;

        // The composite's input, kept as the texture rather than an address: the white is
        // now known to be exactly uniform, so the composite is faithfully computing 0/0
        // and the question is what makes THIS flat on a fifth of frames. Identity was
        // already shown identical across outcomes, so only contents can differ.
        private static Texture _frameSceneTex;

        // Does anything write the composite's input AFTER the composite has read it?
        // Every "the input is not flat" result here sampled that texture at present, which
        // is after every pass in the frame - so a later writer would mean the probe never
        // saw what the shader actually read. Ordering is tracked with a counter rather than
        // by moving the sample, because sampling at the draw needs the render encoder to
        // end and restart, which is the read-after-write split already known to move the
        // flash rate: the probe would perturb what it measures.
        private static IntPtr _compositeInputRoot;
        private static long _compositeSeq = -1;
        private static long _frameSeq;
        private static bool _frameInputWrittenAfter;
        private static string _frameLateWriter;
        private static long _flatLateWrites, _normalLateWrites;

        // How many resources the composite's draw actually declares to the encoder. Read
        // from the code the pairing looks exact, but the argument buffer's allocation
        // strategy moves the flash rate by eleven points for a reason that is neither
        // premature deletion nor a missing declaration by inspection - so count what is
        // issued rather than what the source says should be.
        private static int _frameResidency = -1;

        // How many times the composite draws in one frame. Nothing established this was
        // once, and the per-frame program signatures show programs repeating - so every
        // input measurement taken here may describe only the last invocation.
        private static int _frameCompositeDraws;
        private static double _flatDrawSum, _normalDrawSum;
        private static long _flatDrawN, _normalDrawN;

        // The argument buffer's own bytes, remembered at the composite draw and re-read at
        // present. Everything so far compared the id Ryujinx computed; this compares the id
        // still sitting in memory when the frame ends against the one written. They differ
        // only if something overwrote the table between the draw being encoded and the GPU
        // reading it - which would point the taps at another texture and produce exactly
        // the uniform fill that is measured, with the real input untouched.
        private const int MaxArgIds = 8;
        private static IntPtr _frameArgPtr;
        private static readonly ulong[] _frameArgExpected = new ulong[MaxArgIds];
        private static int _frameArgCount;
        private static long _flatArgMismatch, _normalArgMismatch, _flatArgChecked, _normalArgChecked;

        public static unsafe void NoteArgBuffer(IntPtr contents, int offset, ReadOnlySpan<ulong> ids)
        {
            if (!Enabled || contents == IntPtr.Zero)
            {
                return;
            }

            _frameArgPtr = (IntPtr)((byte*)contents + offset);
            _frameArgCount = Math.Min(ids.Length, MaxArgIds);

            for (int i = 0; i < _frameArgCount; i++)
            {
                _frameArgExpected[i] = ids[i];
            }
        }
        private static double _flatResidencySum, _normalResidencySum;
        private static long _flatResidencyN, _normalResidencyN;

        /// <summary>
        /// The composite's own colour attachment. The divide-by-zero reading is refuted, so
        /// there is no longer any reason to believe the composite is what makes the frame
        /// white - and if its output is not uniform while the presented surface is, the
        /// white arrives after it, somewhere nobody has looked.
        /// </summary>
        public static void NoteCompositeOutput(Texture target)
        {
            if (Enabled && target != null)
            {
                _frameSceneTex = target;
            }
        }

        public static void NoteResidency(int count)
        {
            if (Enabled)
            {
                _frameResidency = count;
            }
        }
        private static readonly Dictionary<string, (long Flat, long Normal)> _lateWriterStats = new();

        private static void NoteWriteOrdering(IntPtr root, string who)
        {
            _frameSeq++;

            if (_compositeSeq >= 0 && root != IntPtr.Zero && root == _compositeInputRoot)
            {
                _frameInputWrittenAfter = true;
                _frameLateWriter = who;
            }
        }

        // The composite's constant buffers, first four floats of each slot. The weight sum
        // does not come from the taps alone: fp_c3 feeds the fetch coordinates and fp_c1
        // carries the Newton constant, so a zeroed or stale buffer produces the same 0/0
        // over a perfectly good texture. Never looked at before, because every binding
        // check compared identities, which a constant buffer keeps while its contents move.
        private const int MaxCbSlots = 32;
        private static readonly float[] _frameCb = new float[MaxCbSlots * 4];
        private static readonly bool[] _frameCbSeen = new bool[MaxCbSlots];
        private static readonly double[] _cbFlatSum = new double[MaxCbSlots * 4];
        private static readonly double[] _cbNormalSum = new double[MaxCbSlots * 4];
        private static readonly float[] _cbFlatMin = new float[MaxCbSlots * 4];
        private static readonly float[] _cbFlatMax = new float[MaxCbSlots * 4];
        private static readonly float[] _cbNormalMin = new float[MaxCbSlots * 4];
        private static readonly float[] _cbNormalMax = new float[MaxCbSlots * 4];
        private static bool _cbExtremaInit;
        private static readonly long[] _cbFlatN = new long[MaxCbSlots];
        private static readonly long[] _cbNormalN = new long[MaxCbSlots];

        /// <summary>
        /// Four vertices a stride apart, compared with each other. A degenerate UV means the
        /// attribute is constant *across* the primitive's vertices, which reading vertex zero
        /// alone cannot see - and vertex zero came out bit-identical on both outcomes, which
        /// settles nothing. Reported as the number of distinct vertices of four.
        /// </summary>
        public static unsafe void NoteVertexSpread(IntPtr contents, int offset, int stride)
        {
            if (!Enabled || contents == IntPtr.Zero || stride <= 0)
            {
                return;
            }

            int distinct = 0;

            for (int i = 0; i < 4; i++)
            {
                ulong a = *(ulong*)((byte*)contents + offset + i * stride);
                bool seen = false;

                for (int j = 0; j < i; j++)
                {
                    if (*(ulong*)((byte*)contents + offset + j * stride) == a)
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    distinct++;
                }
            }

            _frameVertexDistinct = distinct;
        }

        /// <summary>
        /// The stride the fetch uses. Four distinct vertices sit in the buffer on a white
        /// frame and the shader still sees a constant attribute, so the collapse is between
        /// them - and a stride of zero makes every vertex read element zero, which is
        /// exactly that.
        /// </summary>
        public static void NoteVertexStride(int stride)
        {
            if (Enabled)
            {
                _frameVertexStride = stride;
            }
        }

        public static void NoteDrawParams(int count, int instances, int first, int indexed)
        {
            if (Enabled)
            {
                _frameDraw = (count, instances, first, indexed);
            }
        }

        private static (int Count, int Inst, int First, int Indexed) _frameDraw = (-1, -1, -1, -1);
        private static readonly Dictionary<string, (long Flat, long Normal)> _drawParamStats = new();
        private static int _frameVertexStride = -1;
        private static double _flatStrideSum, _normalStrideSum;
        private static long _flatStrideN, _normalStrideN;
        private static int _flatStrideMin = int.MaxValue, _flatStrideMax = int.MinValue;
        private static int _frameVertexDistinct = -1;
        private static double _flatVertSum, _normalVertSum;
        private static long _flatVertN, _normalVertN;

        public static unsafe void NoteCompositeConstants(int slot, IntPtr contents, int offset)
        {
            if (!Enabled || contents == IntPtr.Zero || (uint)slot >= MaxCbSlots)
            {
                return;
            }

            float* f = (float*)((byte*)contents + offset);

            for (int i = 0; i < 4; i++)
            {
                _frameCb[slot * 4 + i] = f[i];
            }

            _frameCbSeen[slot] = true;

            // Slot 0 is the support buffer, and its first four floats are the alpha-test
            // field, which is why reading them showed nothing but zeros. What matters is
            // render_scale: TexelFetchScale multiplies the fetch coordinate by
            // render_scale[texture + 1].x, returning the coordinate untouched only when
            // that is exactly 1.0. A wrong scale sends every tap somewhere the frame never
            // wrote - inside the texture, since the scene target is allocated at 1600x896
            // and rendered at 800x448, which is why clamping to texture bounds and
            // injecting content both changed nothing. Parked in the last slot so the
            // reporting path needs no new plumbing.
            if (slot == 0)
            {
                float* rs = (float*)((byte*)contents + offset + SupportBuffer.GraphicsRenderScaleOffset);

                _frameCb[(MaxCbSlots - 1) * 4 + 0] = rs[0];                  // render_scale[0].x
                _frameCb[(MaxCbSlots - 1) * 4 + 1] = rs[SupportBuffer.FieldSize / sizeof(float)];
                _frameCbSeen[MaxCbSlots - 1] = true;
            }
        }

        // The programs that sampled a scene-class texture this frame, in order. The
        // count alone already steps the rate hard - zero flat in 803 frames at eleven
        // or fewer, 37.6% at thirteen - so what those draws actually are is the next
        // question, and diffing the eleven-draw signature against the thirteen-draw one
        // names the stages whose presence coincides with the fault.
        private const int MaxFrameProgs = 40;
        private const int MaxSignatures = 200;
        private static readonly string[] _frameProgs = new string[MaxFrameProgs];
        private static int _frameProgCount;
        private static readonly Dictionary<string, (long Flat, long Normal)> _signatureStats = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _bindingStats = new();
        private static long _framesWithNoBinding;

        // Mean luma of the sampled grid, split by outcome. Without it a run whose
        // picture went black reports zero flat frames and reads as a fix - which is
        // exactly how a "this build might suppress it" result was once produced.
        private static double _lumaFlatSum, _lumaNormalSum;

        // Shape of the sampled grid on flat frames: the per-frame darkest and brightest
        // sample, the spread, and how many of the grid actually saturated. Kept separately
        // for normal frames so the two can be read against each other.
        private static double _flatMinSum, _flatMaxSum, _flatSdSum, _flatSatSum;
        private static double _normalMinSum, _normalMaxSum, _normalSdSum;

        // Distinct raw 32-bit values among the composite's 25 input samples. One means
        // the input it averages over is a single colour, which is exactly the condition
        // that drives the weight sum to zero.
        private static double _flatInputDistinctSum, _normalInputDistinctSum;
        private static long _flatInputFrames, _normalInputFrames;

        // What a viewer actually counts is flashes, not flat frames. Consecutive flat
        // frames are one flash; a change that halves the flat *frames* while leaving the
        // number of runs alone looks like no change at all, which is what was reported
        // after the read-after-write split took 45% to 24%. Runs, run length and the gap
        // between them are the perceptual quantities, so measure those too.
        private static bool _prevFlat;
        private static long _runs;
        private static long _runFrames;
        private static long _longestRun;
        private static long _currentRun;
        private static long _gapSum;
        private static long _currentGap;
        private static long _gaps;

        // Two questions the captures could not settle by eye, asked as numbers instead.
        //
        // The HUD renders correctly over the white on a flat frame, and the hardware
        // coverage counter says the composite covered all 2,073,600 pixels, so the
        // presented surface *is* written during the frame - which sits badly with three
        // GPU captures in which no encoder's attachment looked white. Judging a
        // RG11B10Float attachment by a thumbnail is not evidence; these are.
        //
        //   identity  - is a *different* storage selected for presentation on flat
        //               frames? That is the "selected already white" reading, and it is
        //               one comparison.
        //   age       - how many frames since that storage was last a colour attachment.
        //               Zero means the frame drew into it; a large value on flat frames
        //               only would mean it was presented stale.
        private static readonly Dictionary<IntPtr, long> _lastAttachmentFrame = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _presentIdentity = new();
        private static readonly Dictionary<long, (long Flat, long Normal)> _presentAge = new();
        private static IntPtr _framePresentRoot;
        private static long _framePresentAge = -1;

        // Which programs drew into the storage that will be presented, recorded for the
        // frame that writes it rather than the frame that shows it. The age table
        // measured 1461 flat and 3911 normal frames at age 1 against 1 and 26 at age 0:
        // the surface shown at frame N was last an attachment at N-1, so the content -
        // and the white - is written a frame before it appears. Every instrument aimed
        // at the frame that displays white, this one included until now, was looking a
        // frame late. A GPU capture of the writing frame showed the presented surface
        // taking only a draw-based clear inside the window, and nothing else; whether
        // that is the whole story on flat frames is what this answers, over thousands of
        // frames instead of one.
        private const int MaxWriters = 24;
        private static readonly string[] _frameWriters = new string[MaxWriters];
        private static int _frameWriterCount;
        private static readonly Dictionary<string, (long Flat, long Normal)> _writerStats = new();
        private static readonly Dictionary<IntPtr, string[]> _pendingWriters = new();
        private static readonly Dictionary<IntPtr, int> _pendingWriterCount = new();

        private static readonly ulong[] _frameShapes = new ulong[8];
        private static int _frameShapeCount;
        private static int _frameUploads;
        private static int _frameBigUploads;
        private static int _frameBigUploadsOntoRt;
        private static int _frameBigCopies;
        private static int _frameBigCopiesOntoRt;

        private static long _frame;
        private static long _dropped;

        // The join. "With" means at least one big upload/copy that frame.
        private static long _flatFrames;
        private static long _normalFrames;
        private static long _flatWithUpload;
        private static long _normalWithUpload;
        private static long _flatWithUploadOntoRt;
        private static long _normalWithUploadOntoRt;
        private static long _flatWithCopy;
        private static long _normalWithCopy;
        private static long _flatWithCopyOntoRt;
        private static long _normalWithCopyOntoRt;

        // Shape key: width<<40 | height<<16 | (kind: 1 upload, 2 copy)<<8 | ontoRt.
        private static readonly Dictionary<ulong, (long Flat, long Normal)> _shapeStats = new();

        private const int ReportInterval = 600;

        public static void Init(MTLDevice device)
        {
            if (!Enabled)
            {
                return;
            }

            _buf = device.NewBuffer(2 * Slots * Pixels * BytesPerPixel, MTLResourceOptions.ResourceStorageModeShared);

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"uploadcorr armed: ring={Slots} grid={GridSide}x{GridSide} satLuma={SaturatedLuma} need={SaturatedNeeded} minPixels={MinPixels}");
        }

        /// <summary>
        /// The resource id written into the composite's argument buffer for the scene
        /// texture. Split by outcome this answers the only question left: a different id
        /// on flat frames means the shader was pointed at something else and the fault is
        /// ours; identical ids mean the driver returned white for a correctly bound,
        /// correctly filled texture.
        /// </summary>
        /// <summary>
        /// The storage actually handed to the window this frame, and how long since
        /// anything rendered into it. Called at present, before the samples are encoded.
        /// </summary>
        public static void NotePresented(Texture src)
        {
            if (!Enabled || src == null)
            {
                return;
            }

            _framePresentRoot = src.CanonicalPtr;
            _framePresentAge = _lastAttachmentFrame.TryGetValue(src.CanonicalPtr, out long last)
                ? _frame - last
                : -1;

            // The writers recorded against this storage since it was last presented -
            // i.e. what drew into it during the frame that produced what is about to be
            // shown.
            _frameWriterCount = _pendingWriterCount.TryGetValue(src.CanonicalPtr, out int wc) ? wc : 0;

            if (_frameWriterCount > 0)
            {
                Array.Copy(_pendingWriters[src.CanonicalPtr], _frameWriters, _frameWriterCount);
            }

            _pendingWriterCount[src.CanonicalPtr] = 0;
        }

        public static void NoteSceneBinding(ulong gpuAddress, IntPtr nativePtr, IntPtr canonicalPtr, string program, Texture storage = null)
        {
            if (!Enabled)
            {
                return;
            }

            // First of the frame, not last. The consumer whose fetch comes back white
            // is the second pass of the frame; every later draw that also samples a
            // scene-class texture used to overwrite this record, so the "identical
            // binding on flat and normal frames" reading was taken from whichever draw
            // happened to be last. Keep both, and report them separately.
            if (_frameBinding.Count == 0)
            {
                _frameBinding.GpuAddress = gpuAddress;
                _frameBinding.NativePtr = nativePtr;
                _frameBinding.CanonicalPtr = canonicalPtr;
                _frameBinding.Program = program;
            }

            _frameBindingLast.GpuAddress = gpuAddress;
            _frameBindingLast.NativePtr = nativePtr;
            _frameBindingLast.CanonicalPtr = canonicalPtr;
            _frameBindingLast.Program = program;
            _frameSceneTex = storage ?? _frameSceneTex;

            if (storage != null)
            {
                _frameCompositeDraws++;
                _compositeInputRoot = storage.CanonicalPtr;
                _compositeSeq = _frameSeq;
            }

            if (_frameProgCount < MaxFrameProgs)
            {
                string label = program ?? "?";
                _frameProgs[_frameProgCount++] = label.Length > 6 ? label[..6] : label;
            }

            _frameBinding.Count++;
        }

        public static void NoteAttachment(Texture target)
        {
            if (!Enabled || target == null)
            {
                return;
            }

            _attachedThisFrame.Add(target.CanonicalPtr);
            _lastAttachmentFrame[target.CanonicalPtr] = _frame;

            // Also a writer entry. NoteAttachmentDraw only fires per draw, so a pass that
            // binds this surface and clears it without drawing leaves no trace - and the
            // presented storage never appears as a draw target at all, while its age says
            // it was an attachment one frame ago. A clear writes a uniform fill by
            // definition, which is the shape of this fault.
            NoteAttachmentDraw(target.CanonicalPtr, "attach");
        }

        /// <summary>
        /// A draw landing on a colour attachment, with the program that issued it.
        /// Accumulated per storage so the frame that writes the presented surface can be
        /// described by what actually drew into it.
        /// </summary>
        public static void NoteAttachmentDraw(IntPtr root, string program)
        {
            NoteWriteOrdering(root, program);

            if (!Enabled || root == IntPtr.Zero)
            {
                return;
            }

            if (!_pendingWriters.TryGetValue(root, out string[] writers))
            {
                writers = new string[MaxWriters];
                _pendingWriters[root] = writers;
                _pendingWriterCount[root] = 0;
            }

            int count = _pendingWriterCount[root];
            string label = program ?? "?";
            label = label.Length > 6 ? label[..6] : label;

            for (int i = 0; i < count; i++)
            {
                if (writers[i] == label)
                {
                    return;
                }
            }

            if (count < MaxWriters)
            {
                writers[count] = label;
                _pendingWriterCount[root] = count + 1;
            }
        }

        public static void NoteUpload(Texture target)
        {
            NoteWriteOrdering(target.CanonicalPtr, "upload");

            Note(target, isCopy: false);
        }

        public static void NoteCopyIn(Texture target)
        {
            NoteWriteOrdering(target.CanonicalPtr, "copy");

            Note(target, isCopy: true);
        }

        private static void Note(Texture target, bool isCopy)
        {
            if (!Enabled || target == null)
            {
                return;
            }

            if (!isCopy)
            {
                _frameUploads++;
            }

            long pixels = (long)target.Info.Width * target.Info.Height;

            if (pixels < MinPixels)
            {
                return;
            }

            bool ontoRt = _attachedThisFrame.Contains(target.CanonicalPtr);

            if (isCopy)
            {
                _frameBigCopies++;

                if (ontoRt)
                {
                    _frameBigCopiesOntoRt++;
                }
            }
            else
            {
                _frameBigUploads++;

                if (ontoRt)
                {
                    _frameBigUploadsOntoRt++;
                }
            }

            ulong shape = ((ulong)(uint)target.Info.Width << 40)
                | ((ulong)(uint)target.Info.Height << 16)
                | ((isCopy ? 2ul : 1ul) << 8)
                | (ontoRt ? 1ul : 0ul);

            for (int i = 0; i < _frameShapeCount; i++)
            {
                if (_frameShapes[i] == shape)
                {
                    return;
                }
            }

            if (_frameShapeCount < _frameShapes.Length)
            {
                _frameShapes[_frameShapeCount++] = shape;
            }
        }

        /// <summary>
        /// Called at present, after the present blit is encoded and before the command
        /// buffer is committed. Consumes any ring slots whose command buffer has
        /// completed, then records this frame's samples and accumulator snapshot.
        /// </summary>
        public static unsafe void OnPresent(CommandBufferScoped cbs, Texture src)
        {
            if (!Enabled || _buf.NativePtr == IntPtr.Zero || src == null)
            {
                return;
            }

            // 1. Classify every slot whose fence already signalled. Never waits.
            for (int i = 0; i < Slots; i++)
            {
                ref Slot slot = ref _slots[i];

                if (slot.Valid && slot.Fence.IsSignaled())
                {
                    Classify(ref slot, i);
                }
            }

            MTLTexture tex = src.GetHandle(cbs);

            if (tex.NativePtr != IntPtr.Zero)
            {
                int idx = (int)(_frame % Slots);
                ref Slot mine = ref _slots[idx];

                if (mine.Valid)
                {
                    // The fence never signalled in a whole ring revolution. Drop the
                    // stale sample rather than wait for it.
                    mine.Fence.Put();
                    mine.Valid = false;
                    _dropped++;
                }

                MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

                for (int i = 0; i < Pixels; i++)
                {
                    blit.CopyFromTexture(
                        tex, 0, 0,
                        new MTLOrigin
                        {
                            x = (ulong)(src.Width * (i % GridSide + 1) / (GridSide + 1)),
                            y = (ulong)(src.Height * (i / GridSide + 1) / (GridSide + 1)),
                            z = 0,
                        },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _buf, (ulong)((idx * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                }

                // Twenty-five more points, from the composite's input. Sampled as raw
                // 32-bit values rather than luma: the scene target is RG11B10Float, so
                // its bytes are not BGRA and only equality is meaningful. A flat input
                // collapses to a single distinct value.
                Texture sceneTex = _frameSceneTex;
                mine.InputSampled = false;

                if (sceneTex != null)
                {
                    MTLTexture stex = sceneTex.GetHandle(cbs);

                    if (stex.NativePtr != IntPtr.Zero && sceneTex.Width > GridSide && sceneTex.Height > GridSide)
                    {
                        for (int i = 0; i < Pixels; i++)
                        {
                            blit.CopyFromTexture(
                                stex, 0, 0,
                                new MTLOrigin
                                {
                                    // Adjacent texels around the centre, not a grid over
                                    // the whole image. Spread sampling already said the
                                    // texture carries a picture (19.72 distinct of 25);
                                    // what the shader actually averages is a 4x3
                                    // neighbourhood of one pixel.
                                    x = (ulong)(sceneTex.Width / 2 + i % GridSide),
                                    y = (ulong)(sceneTex.Height / 2 + i / GridSide),
                                    z = 0,
                                },
                                new MTLSize { width = 1, height = 1, depth = 1 },
                                _buf, (ulong)(((Slots + idx) * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                        }

                        mine.InputSampled = true;
                    }
                }

                mine.InputWrittenAfter = _frameInputWrittenAfter;
                mine.Residency = _frameResidency;
                mine.CompositeDraws = _frameCompositeDraws;
                mine.VertexDistinct = _frameVertexDistinct;
                mine.VertexStride = _frameVertexStride;
                mine.Draw = _frameDraw;
                mine.ArgPtr = _frameArgPtr;
                mine.ArgCount = _frameArgCount;

                if (_frameArgCount > 0)
                {
                    mine.ArgExpected ??= new ulong[MaxArgIds];
                    Array.Copy(_frameArgExpected, mine.ArgExpected, _frameArgCount);
                }
                mine.LateWriter = _frameLateWriter;
                mine.Cb ??= new float[MaxCbSlots * 4];
                mine.CbSeen ??= new bool[MaxCbSlots];
                Array.Copy(_frameCb, mine.Cb, _frameCb.Length);
                Array.Copy(_frameCbSeen, mine.CbSeen, _frameCbSeen.Length);

                mine.Fence = cbs.GetFence();
                mine.Fence.Get();
                mine.Frame = _frame;
                mine.Uploads = _frameUploads;
                mine.BigUploads = _frameBigUploads;
                mine.BigUploadsOntoRt = _frameBigUploadsOntoRt;
                mine.BigCopies = _frameBigCopies;
                mine.BigCopiesOntoRt = _frameBigCopiesOntoRt;
                mine.Shapes ??= new ulong[8];
                Array.Copy(_frameShapes, mine.Shapes, _frameShapeCount);
                mine.ShapeCount = _frameShapeCount;
                mine.Binding = _frameBinding;
                mine.BindingLast = _frameBindingLast;
                mine.Signature = $"n={_frameBinding.Count} " + string.Join(",", _frameProgs, 0, _frameProgCount);
                mine.PresentRoot = _framePresentRoot;
                mine.PresentAge = _framePresentAge;
                mine.Writers = _frameWriterCount == 0
                    ? "<none>"
                    : string.Join(",", _frameWriters, 0, _frameWriterCount);
                mine.Valid = true;
            }

            // 2. Reset the frame accumulators. The attachment set is per frame too.
            _attachedThisFrame.Clear();
            _frameBinding = default;
            _frameBindingLast = default;
            _frameSceneTex = null;
            _compositeInputRoot = IntPtr.Zero;
            _compositeSeq = -1;
            _frameSeq = 0;
            _frameInputWrittenAfter = false;
            _frameResidency = -1;
            _frameCompositeDraws = 0;
            _frameVertexDistinct = -1;
            _frameVertexStride = -1;
            _frameDraw = (-1, -1, -1, -1);
            _frameArgPtr = IntPtr.Zero;
            _frameArgCount = 0;
            _frameLateWriter = null;
            Array.Clear(_frameCbSeen);
            _frameProgCount = 0;
            _framePresentRoot = IntPtr.Zero;
            _framePresentAge = -1;
            _frameShapeCount = 0;
            _frameUploads = 0;
            _frameBigUploads = 0;
            _frameBigUploadsOntoRt = 0;
            _frameBigCopies = 0;
            _frameBigCopiesOntoRt = 0;

            _frame++;

            if (_frame % ReportInterval == 0)
            {
                Report();
            }
        }

        private static unsafe void Classify(ref Slot slot, int index)
        {
            byte* p = (byte*)_buf.Contents + index * Pixels * BytesPerPixel;
            int saturated = 0;
            double lumaSum = 0;
            double lumaSqSum = 0;
            double lumaMin = 255;
            double lumaMax = 0;

            for (int i = 0; i < Pixels; i++)
            {
                byte* px = p + i * BytesPerPixel;
                double luma = (px[0] + px[1] + px[1] + px[2]) * 0.25;

                lumaSum += luma;
                lumaSqSum += luma * luma;

                if (luma < lumaMin)
                {
                    lumaMin = luma;
                }

                if (luma > lumaMax)
                {
                    lumaMax = luma;
                }

                if (luma >= SaturatedLuma)
                {
                    saturated++;
                }
            }

            bool flat = saturated >= SaturatedNeeded;

            int inputDistinct = 0;

            if (slot.InputSampled)
            {
                uint* q = (uint*)((byte*)_buf.Contents + (Slots + index) * Pixels * BytesPerPixel);

                for (int i = 0; i < Pixels; i++)
                {
                    bool seen = false;

                    for (int j = 0; j < i; j++)
                    {
                        if (q[j] == q[i])
                        {
                            seen = true;
                            break;
                        }
                    }

                    if (!seen)
                    {
                        inputDistinct++;
                    }
                }
            }

            // The shape of the white, not just the fact of it. Everything downstream of
            // this classifier has been asking who wrote white; nothing has asked what the
            // white looks like, and the two answers point at completely different faults.
            // A uniform fill - an undefined allocation, a clear, a discarded store - has
            // min == max and no spread. A real image driven to saturation by a bad
            // exposure or tonemap keeps its dark pixels and its structure, so min stays
            // well below max and the spread survives.
            double mean = lumaSum / Pixels;
            double variance = (lumaSqSum / Pixels) - (mean * mean);
            double sd = variance > 0 ? Math.Sqrt(variance) : 0;

            if (slot.ArgCount > 0 && slot.ArgPtr != IntPtr.Zero)
            {
                ulong* now = (ulong*)slot.ArgPtr;
                bool differs = false;

                for (int i = 0; i < slot.ArgCount; i++)
                {
                    if (now[i] != slot.ArgExpected[i])
                    {
                        differs = true;
                        break;
                    }
                }

                if (flat)
                {
                    _flatArgChecked++;
                    if (differs) { _flatArgMismatch++; }
                }
                else
                {
                    _normalArgChecked++;
                    if (differs) { _normalArgMismatch++; }
                }
            }

            if (flat)
            {
                _flatDrawSum += slot.CompositeDraws;
                _flatDrawN++;
            }
            else
            {
                _normalDrawSum += slot.CompositeDraws;
                _normalDrawN++;
            }

            if (slot.Draw.Count >= 0 && _drawParamStats.Count < 32)
            {
                string k = $"count={slot.Draw.Count} inst={slot.Draw.Inst} first={slot.Draw.First} indexed={slot.Draw.Indexed}";
                (long df, long dn) = _drawParamStats.TryGetValue(k, out (long Flat, long Normal) dv) ? (dv.Flat, dv.Normal) : (0L, 0L);
                _drawParamStats[k] = flat ? (df + 1, dn) : (df, dn + 1);
            }

            if (slot.VertexStride >= 0)
            {
                if (flat)
                {
                    _flatStrideSum += slot.VertexStride;
                    _flatStrideN++;
                    if (slot.VertexStride < _flatStrideMin) { _flatStrideMin = slot.VertexStride; }
                    if (slot.VertexStride > _flatStrideMax) { _flatStrideMax = slot.VertexStride; }
                }
                else
                {
                    _normalStrideSum += slot.VertexStride;
                    _normalStrideN++;
                }
            }

            if (slot.VertexDistinct >= 0)
            {
                if (flat) { _flatVertSum += slot.VertexDistinct; _flatVertN++; }
                else { _normalVertSum += slot.VertexDistinct; _normalVertN++; }
            }

            if (slot.Residency >= 0)
            {
                if (flat)
                {
                    _flatResidencySum += slot.Residency;
                    _flatResidencyN++;
                }
                else
                {
                    _normalResidencySum += slot.Residency;
                    _normalResidencyN++;
                }
            }

            if (slot.InputWrittenAfter)
            {
                if (flat)
                {
                    _flatLateWrites++;
                }
                else
                {
                    _normalLateWrites++;
                }

                if (slot.LateWriter != null && _lateWriterStats.Count < MaxSignatures)
                {
                    _lateWriterStats.TryGetValue(slot.LateWriter, out (long Flat, long Normal) w);
                    _lateWriterStats[slot.LateWriter] = flat ? (w.Flat + 1, w.Normal) : (w.Flat, w.Normal + 1);
                }
            }

            if (slot.CbSeen != null)
            {
                if (!_cbExtremaInit)
                {
                    _cbExtremaInit = true;

                    for (int i = 0; i < MaxCbSlots * 4; i++)
                    {
                        _cbFlatMin[i] = _cbNormalMin[i] = float.MaxValue;
                        _cbFlatMax[i] = _cbNormalMax[i] = float.MinValue;
                    }
                }

                double[] into = flat ? _cbFlatSum : _cbNormalSum;
                long[] n = flat ? _cbFlatN : _cbNormalN;
                float[] lo = flat ? _cbFlatMin : _cbNormalMin;
                float[] hi = flat ? _cbFlatMax : _cbNormalMax;

                for (int c = 0; c < MaxCbSlots; c++)
                {
                    if (!slot.CbSeen[c])
                    {
                        continue;
                    }

                    n[c]++;

                    for (int i = 0; i < 4; i++)
                    {
                        float v = slot.Cb[c * 4 + i];
                        into[c * 4 + i] += v;

                        if (v < lo[c * 4 + i])
                        {
                            lo[c * 4 + i] = v;
                        }

                        if (v > hi[c * 4 + i])
                        {
                            hi[c * 4 + i] = v;
                        }
                    }
                }
            }

            if (flat)
            {
                if (slot.InputSampled)
                {
                    _flatInputDistinctSum += inputDistinct;
                    _flatInputFrames++;
                }

                _flatMinSum += lumaMin;
                _flatMaxSum += lumaMax;
                _flatSdSum += sd;
                _flatSatSum += saturated;
                _lumaFlatSum += lumaSum / Pixels;
                _runFrames++;
                _currentRun++;

                if (!_prevFlat)
                {
                    _runs++;

                    if (_currentGap > 0)
                    {
                        _gapSum += _currentGap;
                        _gaps++;
                        _currentGap = 0;
                    }
                }
            }
            else
            {
                if (slot.InputSampled)
                {
                    _normalInputDistinctSum += inputDistinct;
                    _normalInputFrames++;
                }

                _normalMinSum += lumaMin;
                _normalMaxSum += lumaMax;
                _normalSdSum += sd;
                _lumaNormalSum += lumaSum / Pixels;
                _currentGap++;

                if (_prevFlat)
                {
                    if (_currentRun > _longestRun)
                    {
                        _longestRun = _currentRun;
                    }

                    _currentRun = 0;
                }
            }

            _prevFlat = flat;

            if (flat)
            {
                _flatFrames++;

                if (slot.BigUploads > 0)
                {
                    _flatWithUpload++;
                }

                if (slot.BigUploadsOntoRt > 0)
                {
                    _flatWithUploadOntoRt++;
                }

                if (slot.BigCopies > 0)
                {
                    _flatWithCopy++;
                }

                if (slot.BigCopiesOntoRt > 0)
                {
                    _flatWithCopyOntoRt++;
                }
            }
            else
            {
                _normalFrames++;

                if (slot.BigUploads > 0)
                {
                    _normalWithUpload++;
                }

                if (slot.BigUploadsOntoRt > 0)
                {
                    _normalWithUploadOntoRt++;
                }

                if (slot.BigCopies > 0)
                {
                    _normalWithCopy++;
                }

                if (slot.BigCopiesOntoRt > 0)
                {
                    _normalWithCopyOntoRt++;
                }
            }

            if (slot.Binding.Count == 0)
            {
                _framesWithNoBinding++;
            }
            else
            {
                string key = $"FIRST n={slot.Binding.Count} gpu=0x{slot.Binding.GpuAddress:X} " +
                             $"tex=0x{slot.Binding.NativePtr:X} root=0x{slot.Binding.CanonicalPtr:X} " +
                             $"prog={slot.Binding.Program}";

                (long f, long n) = _bindingStats.TryGetValue(key, out (long Flat, long Normal) b) ? (b.Flat, b.Normal) : (0L, 0L);
                _bindingStats[key] = flat ? (f + 1, n) : (f, n + 1);

                string lastKey = $"LAST  gpu=0x{slot.BindingLast.GpuAddress:X} " +
                                 $"tex=0x{slot.BindingLast.NativePtr:X} root=0x{slot.BindingLast.CanonicalPtr:X} " +
                                 $"prog={slot.BindingLast.Program}";

                (long lf, long ln) = _bindingStats.TryGetValue(lastKey, out (long Flat, long Normal) lb) ? (lb.Flat, lb.Normal) : (0L, 0L);
                _bindingStats[lastKey] = flat ? (lf + 1, ln) : (lf, ln + 1);
            }

            if (slot.PresentRoot != IntPtr.Zero)
            {
                string key = $"0x{slot.PresentRoot:X}";
                (long pf, long pn) = _presentIdentity.TryGetValue(key, out (long Flat, long Normal) pv) ? (pv.Flat, pv.Normal) : (0L, 0L);
                _presentIdentity[key] = flat ? (pf + 1, pn) : (pf, pn + 1);

                (long af, long an) = _presentAge.TryGetValue(slot.PresentAge, out (long Flat, long Normal) av) ? (av.Flat, av.Normal) : (0L, 0L);
                _presentAge[slot.PresentAge] = flat ? (af + 1, an) : (af, an + 1);

                if (slot.Writers != null && (_writerStats.Count < 64 || _writerStats.ContainsKey(slot.Writers)))
                {
                    (long wf, long wn) = _writerStats.TryGetValue(slot.Writers, out (long Flat, long Normal) wv) ? (wv.Flat, wv.Normal) : (0L, 0L);
                    _writerStats[slot.Writers] = flat ? (wf + 1, wn) : (wf, wn + 1);
                }
            }

            if (slot.Signature != null &&
                (_signatureStats.Count < MaxSignatures || _signatureStats.ContainsKey(slot.Signature)))
            {
                (long sf, long sn) = _signatureStats.TryGetValue(slot.Signature, out (long Flat, long Normal) sv)
                    ? (sv.Flat, sv.Normal)
                    : (0L, 0L);

                _signatureStats[slot.Signature] = flat ? (sf + 1, sn) : (sf, sn + 1);
            }

            for (int i = 0; i < slot.ShapeCount; i++)
            {
                (long f, long n) = _shapeStats.TryGetValue(slot.Shapes[i], out (long Flat, long Normal) v) ? (v.Flat, v.Normal) : (0L, 0L);
                _shapeStats[slot.Shapes[i]] = flat ? (f + 1, n) : (f, n + 1);
            }

            slot.Fence.Put();
            slot.Valid = false;
        }

        private static void Report()
        {
            StringBuilder sb = new();

            sb.Append($"uploadcorr: classified={_flatFrames + _normalFrames} flat={_flatFrames} normal={_normalFrames} dropped={_dropped}");
            sb.Append($" | luma flat {(_flatFrames > 0 ? _lumaFlatSum / _flatFrames : 0):F0}, normal {(_normalFrames > 0 ? _lumaNormalSum / _normalFrames : 0):F0}");
            sb.Append($" | shape flat min {(_flatFrames > 0 ? _flatMinSum / _flatFrames : 0):F0} max {(_flatFrames > 0 ? _flatMaxSum / _flatFrames : 0):F0} sd {(_flatFrames > 0 ? _flatSdSum / _flatFrames : 0):F1} sat {(_flatFrames > 0 ? _flatSatSum / _flatFrames : 0):F1}/{Pixels}");
            sb.Append($", normal min {(_normalFrames > 0 ? _normalMinSum / _normalFrames : 0):F0} max {(_normalFrames > 0 ? _normalMaxSum / _normalFrames : 0):F0} sd {(_normalFrames > 0 ? _normalSdSum / _normalFrames : 0):F1}");
            sb.Append($" | input distinct flat {(_flatInputFrames > 0 ? _flatInputDistinctSum / _flatInputFrames : 0):F2}/{Pixels} over {_flatInputFrames}");
            sb.Append($", normal {(_normalInputFrames > 0 ? _normalInputDistinctSum / _normalInputFrames : 0):F2}/{Pixels} over {_normalInputFrames}");

            foreach (KeyValuePair<string, (long Flat, long Normal)> d in _drawParamStats)
            {
                sb.Append($"\n  blit draw {d.Key}: flat {d.Value.Flat}, normal {d.Value.Normal}");
            }

            sb.Append($" | blit stride: flat {(_flatStrideN > 0 ? _flatStrideSum / _flatStrideN : 0):F1} [{(_flatStrideN > 0 ? _flatStrideMin : 0)},{(_flatStrideN > 0 ? _flatStrideMax : 0)}] over {_flatStrideN}, normal {(_normalStrideN > 0 ? _normalStrideSum / _normalStrideN : 0):F1} over {_normalStrideN}");
            sb.Append($" | blit vertex spread (distinct of 4): flat {(_flatVertN > 0 ? _flatVertSum / _flatVertN : 0):F2} over {_flatVertN}, normal {(_normalVertN > 0 ? _normalVertSum / _normalVertN : 0):F2} over {_normalVertN}");
            sb.Append($" | composite draws/frame: flat {(_flatDrawN > 0 ? _flatDrawSum / _flatDrawN : 0):F2}, normal {(_normalDrawN > 0 ? _normalDrawSum / _normalDrawN : 0):F2}");
            sb.Append($" | argbuf overwritten by frame end: flat {_flatArgMismatch}/{_flatArgChecked}, normal {_normalArgMismatch}/{_normalArgChecked}");
            sb.Append($" | composite residency decls: flat {(_flatResidencyN > 0 ? _flatResidencySum / _flatResidencyN : 0):F2} over {_flatResidencyN}, normal {(_normalResidencyN > 0 ? _normalResidencySum / _normalResidencyN : 0):F2} over {_normalResidencyN}");
            sb.Append($" | input written after the composite read it: flat {_flatLateWrites}/{_flatFrames}, normal {_normalLateWrites}/{_normalFrames}");

            foreach ((string who, (long Flat, long Normal) w) in _lateWriterStats)
            {
                sb.Append($"\n  late writer {who}: flat {w.Flat}, normal {w.Normal}");
            }

            for (int c = 0; c < MaxCbSlots; c++)
            {
                if (_cbFlatN[c] == 0 && _cbNormalN[c] == 0)
                {
                    continue;
                }

                sb.Append($"\n  cb{c}: flat[");
                for (int i = 0; i < 4; i++)
                {
                    sb.Append($"{(_cbFlatN[c] > 0 ? _cbFlatSum[c * 4 + i] / _cbFlatN[c] : 0):G6}{(i < 3 ? " " : string.Empty)}");
                }
                sb.Append($"] n={_cbFlatN[c]}  normal[");
                for (int i = 0; i < 4; i++)
                {
                    sb.Append($"{(_cbNormalN[c] > 0 ? _cbNormalSum[c * 4 + i] / _cbNormalN[c] : 0):G6}{(i < 3 ? " " : string.Empty)}");
                }
                sb.Append($"] n={_cbNormalN[c]}");

                // Extrema, because a mean cannot separate "always this value on flat
                // frames" from "flat frames are the subset that happened to have it".
                if (_cbFlatN[c] > 0 && _cbNormalN[c] > 0)
                {
                    sb.Append("\n        range flat");
                    for (int i = 0; i < 4; i++)
                    {
                        sb.Append($" [{_cbFlatMin[c * 4 + i]:G7},{_cbFlatMax[c * 4 + i]:G7}]");
                    }
                    sb.Append("  normal");
                    for (int i = 0; i < 4; i++)
                    {
                        sb.Append($" [{_cbNormalMin[c * 4 + i]:G7},{_cbNormalMax[c * 4 + i]:G7}]");
                    }
                }
            }
            sb.Append($" | upload: flat {_flatWithUpload}/{_flatFrames}, normal {_normalWithUpload}/{_normalFrames}");
            sb.Append($" | uploadOntoRT: flat {_flatWithUploadOntoRt}, normal {_normalWithUploadOntoRt}");
            sb.Append($" | copy: flat {_flatWithCopy}/{_flatFrames}, normal {_normalWithCopy}/{_normalFrames}");
            sb.Append($" | copyOntoRT: flat {_flatWithCopyOntoRt}, normal {_normalWithCopyOntoRt}");

            sb.Append($"\n  scene bindings seen: {_bindingStats.Count}, frames with none: {_framesWithNoBinding}");

            foreach (KeyValuePair<string, (long Flat, long Normal)> pair in _bindingStats)
            {
                sb.Append($"\n    flat {pair.Value.Flat,6}  normal {pair.Value.Normal,6}   {pair.Key}");
            }

            sb.Append($"\n  FLASHES: {_runs} runs, {(_runs > 0 ? (double)_runFrames / _runs : 0):F2} frames each, " +
                      $"longest {_longestRun}, mean gap {(_gaps > 0 ? (double)_gapSum / _gaps : 0):F1} frames" +
                      $" -> {(_runs > 0 && _flatFrames + _normalFrames > 0 ? _runs * 30.0 / (_flatFrames + _normalFrames) : 0):F2} flashes/sec at 30fps");

            sb.Append("\n  presented storage, by outcome:");

            foreach (KeyValuePair<string, (long Flat, long Normal)> pair in _presentIdentity)
            {
                sb.Append($"\n    flat {pair.Value.Flat,6}  normal {pair.Value.Normal,6}   root={pair.Key}");
            }

            sb.Append("\n  what drew into the presented storage, by outcome:");

            List<KeyValuePair<string, (long Flat, long Normal)>> writers = new(_writerStats);
            writers.Sort((a, b) => (b.Value.Flat + b.Value.Normal).CompareTo(a.Value.Flat + a.Value.Normal));

            for (int i = 0; i < writers.Count && i < 10; i++)
            {
                long total = writers[i].Value.Flat + writers[i].Value.Normal;
                sb.Append($"\n    flat {writers[i].Value.Flat,6} / {total,6} = {(total > 0 ? 100.0 * writers[i].Value.Flat / total : 0),5:F1}%  {writers[i].Key}");
            }

            sb.Append("\n  frames since that storage was last an attachment (-1 = never):");

            foreach (KeyValuePair<long, (long Flat, long Normal)> pair in _presentAge)
            {
                sb.Append($"\n    flat {pair.Value.Flat,6}  normal {pair.Value.Normal,6}   age={pair.Key}");
            }

            sb.Append($"\n  scene-sampling signatures: {_signatureStats.Count}");

            List<KeyValuePair<string, (long Flat, long Normal)>> top = new(_signatureStats);
            top.Sort((a, b) => (b.Value.Flat + b.Value.Normal).CompareTo(a.Value.Flat + a.Value.Normal));

            for (int i = 0; i < top.Count && i < 12; i++)
            {
                long total = top[i].Value.Flat + top[i].Value.Normal;
                sb.Append($"\n    flat {top[i].Value.Flat,6} / {total,6} = {(total > 0 ? 100.0 * top[i].Value.Flat / total : 0),5:F1}%  {top[i].Key}");
            }

            foreach (KeyValuePair<ulong, (long Flat, long Normal)> pair in _shapeStats)
            {
                int width = (int)(pair.Key >> 40);
                int height = (int)((pair.Key >> 16) & 0xFFFFFF);
                bool isCopy = ((pair.Key >> 8) & 0xFF) == 2;
                bool ontoRt = (pair.Key & 0xFF) != 0;

                sb.Append($"\n  {(isCopy ? "copy" : "upload")}{(ontoRt ? "+RT" : "")} {width}x{height}: flat {pair.Value.Flat}, normal {pair.Value.Normal}");
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu, sb.ToString());
        }
    }
}
