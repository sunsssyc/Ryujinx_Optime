using Ryujinx.Common.Logging;
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
        }

        /// <summary>
        /// A draw landing on a colour attachment, with the program that issued it.
        /// Accumulated per storage so the frame that writes the presented surface can be
        /// described by what actually drew into it.
        /// </summary>
        public static void NoteAttachmentDraw(IntPtr root, string program)
        {
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
            Note(target, isCopy: false);
        }

        public static void NoteCopyIn(Texture target)
        {
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
                                    x = (ulong)(sceneTex.Width * (i % GridSide + 1) / (GridSide + 1)),
                                    y = (ulong)(sceneTex.Height * (i / GridSide + 1) / (GridSide + 1)),
                                    z = 0,
                                },
                                new MTLSize { width = 1, height = 1, depth = 1 },
                                _buf, (ulong)(((Slots + idx) * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                        }

                        mine.InputSampled = true;
                    }
                }

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
