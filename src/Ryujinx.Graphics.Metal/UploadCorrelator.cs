using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
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
            public bool AfterSampled;
            public bool SamplerSampled;
            public MTLPixelFormat InputFmt;
            public bool InputWrittenAfter;
            public int Residency;
            public int CompositeDraws;
            public int VertexDistinct;
            public int VertexStride;
            public (int Count, int Inst, int First, int Indexed) Draw;
            public string Indices;
            public string Attrib;
            public string Raster;
            public int WriterCb;
            public long WriterRent;
            public int BlitCb;
            public long BlitRent;
            public bool PsoFresh;
            public string ImgWriters;
            public long InSerial;
            public long WrSerial;
            public long InGen;
            public long WrGen;
            public float[] RowW;
            public bool[] RowSeen;
            public int OutOfOrder;
            public IntPtr ArgPtr;
            public bool PresentMatchesLastRt;
            public bool PresentMatchKnown;
            public bool Rgba8Known;
            public bool Rgba8Match;
            public bool BlitPrevKnown;
            public string Triple;
            public bool SrcWasRecentDst;
            public bool RtSampled;
            public int ReplaceViews;
            public int NewTextures;
            public string ModifiedBy;
            public string RtWriters;
            public string SceneWriters;
            public bool BlitPrevMatch;
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

        // The capture showed the present draw sampling a texture object DIFFERENT from the
        // one the frame's last full-resolution pass rendered into (0x9060fa300 white vs
        // 0x904c49900 correct). This records the last 1920x1080 colour attachment root
        // each frame and compares it, at present, with the object present was handed.
        private static IntPtr _frameLastFullResRt;
        private static Texture _frameLastFullResTex;
        private static IntPtr _frameLastRgba8Rt;
        private static long _rgbaMatchFlat, _rgbaMatchNormal, _rgbaMismFlat, _rgbaMismNormal, _rgbaNoneFlat, _rgbaNoneNormal;
        private static long _rtPicSrcWhiteFlat, _rtPicSrcWhiteNormal, _bothPicFlat, _bothPicNormal, _bothWhiteFlat, _bothWhiteNormal, _rtWhiteSrcPicFlat, _rtWhiteSrcPicNormal;
        private static long _presentMatchFlat, _presentMatchNormal, _presentMismatchFlat, _presentMismatchNormal;

        private static long _presentSrcSerial, _prevPresentSrcSerial;
        private static IntPtr _presentSrcRoot, _presentDstRoot;
        private static int _presentSrcW, _presentSrcH;
        private static readonly Dictionary<string, (long Flat, long Normal)> _tripleStats = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _rtWriterStats = new();

        private static readonly Queue<IntPtr> _recentDstRoots = new();
        private static long _srcWasRecentDstFlat, _srcWasRecentDstNormal, _srcNotDstFlat, _srcNotDstNormal;
        private static bool _frameSrcWasRecentDst;

        public static void NotePresentTriple(IntPtr srcRoot, IntPtr dstRoot, int w, int h, long srcSerial)
        {
            if (!Enabled) { return; }

            // Is what present READS one of the drawables present recently WROTE? The
            // writer census on src reports only the present program itself as a writer,
            // which is impossible unless src is a former dst.
            _frameSrcWasRecentDst = _recentDstRoots.Contains(srcRoot);
            _recentDstRoots.Enqueue(dstRoot);
            while (_recentDstRoots.Count > 8) { _recentDstRoots.Dequeue(); }
            _prevPresentSrcSerial = _presentSrcSerial;
            _presentSrcSerial = srcSerial;
            _presentSrcRoot = srcRoot;
            _presentDstRoot = dstRoot;
            _presentSrcW = w; _presentSrcH = h;
        }

        private static IntPtr _frameSceneSourceRoot;
        private static readonly Dictionary<string, (long Flat, long Normal)> _sceneWriterStats = new();

        public static void NoteSceneSourceForCensus(Texture t)
        {
            if (Enabled && t != null) { _frameSceneSourceRoot = t.CanonicalPtr; }
        }

        private static IntPtr _frameGameRtRoot;
        private static IntPtr _prevGameRtRoot;
        private static bool _gameRtPassOpen, _gameRtPassClear;
        private static long _gameRtPassDrawStart, _drawsSeen;
        private static int _gameRtPassLogs, _gameRtEndLogs;
        private static string _prevFrameGameRtWriters = "(unknown)";
        private static Texture _frameGameRtTex;

        private static int _gameRtLogs;

        private static int _viewLogs;

        public static void NoteGameFinalTargetView(Texture t)
        {
            if (!Enabled || t == null) { return; }
            if (++_viewLogs % 300 == 1)
            {
                bool inCensus = _pendingWriterCount.TryGetValue(t.CanonicalPtr, out int c) && c > 0;
                string w = inCensus ? string.Join(",", _pendingWriters[t.CanonicalPtr], 0, Math.Min(c, MaxWriters)) : "(none)";
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"game RT VIEW: {t.Width}x{t.Height} {t.Info.Format} canon=0x{t.CanonicalPtr:X} native=0x{t.GetHandle().NativePtr:X} sameRootAsRT={(t.CanonicalPtr == _frameGameRtRoot)} writers={w}");
            }
        }

        private static int _frameReplaceViews, _frameNewTextures;
        private static double _rvFlat, _rvNormal, _ntFlat, _ntNormal; private static long _topoFlatN, _topoNormalN;

        private static string _frameModifiedBy = "";
        private static readonly Dictionary<string, (long Flat, long Normal)> _modByStats = new();

        public static void NoteModifiedBy(string who)
        {
            if (Enabled && !_frameModifiedBy.Contains(who)) { _frameModifiedBy += who + ","; }
        }

        public static void NoteTopologyEvent(int kind)
        {
            if (!Enabled) { return; }
            if (kind == 1) { _frameReplaceViews++; } else { _frameNewTextures++; }
        }

        public static void NoteGameFinalTarget(Texture t)
        {
            if (Enabled && t != null)
            {
                _prevGameRtRoot = _frameGameRtRoot;
                _frameGameRtRoot = t.CanonicalPtr;
                _frameGameRtTex = t;

                if (++_gameRtLogs % 600 == 1)
                {
                    // Is this object's root among this frame's attachment roots at all?
                    bool everAttached = _lastAttachmentFrame.TryGetValue(t.CanonicalPtr, out long lf);
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"game RT: {t.Width}x{t.Height} {t.Info.Format} canon=0x{t.CanonicalPtr:X} native=0x{t.GetHandle().NativePtr:X} " +
                        $"isView={(t.CanonicalPtr != t.GetHandle().NativePtr)} everAttached={everAttached} lastAttachFrame={(everAttached ? lf : -1)} nowFrame={_frame} " +
                        $"attachedRootsThisFrame={_attachedThisFrame.Count}");
                }
            }
        }

        public static void NoteFullResAttachment(Texture t)
        {
            if (Enabled && t != null && t.Width >= 1900 && t.Height >= 1000 && !t.Info.Format.IsDepthOrStencil)
            {
                _frameLastFullResRt = t.CanonicalPtr;
                _frameLastFullResTex = t;

                // The present source is RGBA8, not RG11B10 - track the last RGBA8 one too.
                if (t.Info.Format == Format.R8G8B8A8Unorm || t.Info.Format == Format.B8G8R8A8Unorm ||
                    t.Info.Format == Format.R8G8B8A8Srgb || t.Info.Format == Format.B8G8R8A8Srgb)
                {
                    _frameLastRgba8Rt = t.CanonicalPtr;
                }
            }
        }

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

                // Also arm the late-write tracker on THIS texture. The conclusion that the
                // blit's source holds a picture on white frames rests on a sample taken at
                // present, after every pass in the frame - and the check for a later writer
                // was only ever wired to the texel-fetch shader's input, never to this one.
                // If something writes this surface between the blit reading it and the frame
                // ending, the sample shows content the blit never saw, and "the source holds
                // a picture" is not a statement about the frame that went white.
                _compositeInputRoot = target.CanonicalPtr;
                _compositeSeq = _frameSeq;
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
                float* v = (float*)((byte*)contents + offset + i * stride);
                _frameVerts[i * 3 + 0] = v[0];
                _frameVerts[i * 3 + 1] = v[1];
                _frameVerts[i * 3 + 2] = v[2];

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
        public static unsafe void NoteIndices(IntPtr contents, int indexType = 2)
        {
            if (!Enabled || contents == IntPtr.Zero)
            {
                return;
            }

            // MTLIndexType: 0 = UInt16, 1 = UInt32. Anything else is this probe's own
            // default for the 32-bit pattern path.
            if (indexType == 0)
            {
                ushort* p16 = (ushort*)contents;
                _frameIndices = $"{p16[0]},{p16[1]},{p16[2]},{p16[3]},{p16[4]},{p16[5]}";
            }
            else
            {
                uint* p32 = (uint*)contents;
                _frameIndices = $"{p32[0]},{p32[1]},{p32[2]},{p32[3]},{p32[4]},{p32[5]}";
            }
        }

        public static void NoteVertexAttrib(int format, int offset, int bufferIndex)
        {
            if (Enabled)
            {
                _frameAttrib = $"fmt={format} off={offset} buf={bufferIndex}";
            }
        }

        public static void NoteOutOfOrderCommit()
        {
            if (Enabled)
            {
                _frameOutOfOrder++;
            }
        }

        private static int _frameOutOfOrder;
        private static double _flatOooSum, _normalOooSum;
        private static long _flatOooN, _normalOooN;
        /// <summary>
        /// Fixed-function state at the blit's draw. Every measurement so far compared data
        /// - buffers, textures, constants, indices - and none compared the raster state,
        /// which is the one axis that changes frame to frame and can null a draw outright.
        /// This backend has prior form: CullBoth once ignored the enable flag and produced
        /// a 0x0 scissor. A blit rasterised away leaves the pass's load contents on screen,
        /// and everything else measures correct because everything else IS correct.
        /// </summary>
        public static void NoteBlitRaster(string state)
        {
            if (Enabled)
            {
                _frameRaster = state;
            }
        }

        /// <summary>
        /// Whether the PSO bound at the blit was first seen this frame. The capped
        /// per-pointer stats showed white frames' pointers arriving after the cap filled -
        /// every visible signature had flat ~0 - which reads as white frames binding
        /// freshly created pipeline objects. Asked directly.
        /// </summary>
        public static void NoteBlitPso(IntPtr pso)
        {
            if (!Enabled)
            {
                return;
            }

            _framePsoFresh = _seenPsos.Add(pso);
        }

        private static readonly HashSet<IntPtr> _seenPsos = new();
        private static bool _framePsoFresh;
        private static long _freshFlat, _freshNormal, _staleFlat, _staleNormal;

        /// <summary>
        /// Storage-image bindings whose storage IS the presented surface. The writer census
        /// covers render attachments; CopyTo and SetData have their own hooks - but a
        /// compute or fragment shader writing through an image (usage 0x17 carries
        /// ShaderWrite, and 15,809 read-write residency declarations go by per 120 frames)
        /// is invisible to every instrument fielded so far. The last uninstrumented way to
        /// put pixels in that texture.
        /// </summary>
        public static IntPtr LastPresentedRoot => _framePresentRoot;

        public static void NoteImageOnPresented(string program)
        {
            if (Enabled && _frameImgWriters.Count < 8)
            {
                _frameImgWriters.Add(program ?? "?");
            }
        }

        private static readonly List<string> _frameImgWriters = new();

        /// <summary>
        /// The creation serial of the texture object the blit samples, against the serial
        /// of the object its writer painted this frame. Equal addresses said "same texture"
        /// for two days; serials cannot be recycled, so a mismatch here means the cache
        /// swapped the host texture between the write and the read, and the blit sampled
        /// an object nobody painted.
        /// </summary>
        public static void NoteBlitInputSerial(long serial) { if (Enabled) { _inputSerial = serial; } }

        private static readonly Dictionary<IntPtr, long> _handleGen = new();

        public static void BumpHandleGen(IntPtr root)
        {
            if (Enabled && root != IntPtr.Zero)
            {
                lock (_handleGen)
                {
                    _handleGen.TryGetValue(root, out long g);
                    _handleGen[root] = g + 1;
                }
            }
        }

        private static long GenOf(IntPtr root)
        {
            lock (_handleGen)
            {
                return _handleGen.TryGetValue(root, out long g) ? g : 0;
            }
        }

        public static void NoteBlitInputGen(IntPtr root) { if (Enabled) { _inputGen = GenOf(root); } }

        /// <summary>
        /// The in-stream witness. A .gputrace of a caught white frame needs Xcode and an
        /// operator; this encodes the same evidence automatically: the blit's input,
        /// photographed by the GPU immediately after the blit's pass ends, against the same
        /// texels photographed at present. Memory that nothing writes in between (measured:
        /// 1/2,496) cannot differ between the two - so "white just after the blit, a
        /// picture at present" is a read-visibility failure witnessed in the act, and "a
        /// picture just after the blit" convicts the draw's execution instead.
        /// </summary>
        public static void ArmAfterBlitSample() { if (Enabled) { _afterBlitArmed = true; } }

        private static bool _afterBlitArmed;

        public static void SampleInputAfterBlit(CommandBufferScoped cbs)
        {
            if (!Enabled || !_afterBlitArmed)
            {
                return;
            }

            _afterBlitArmed = false;

            Texture sceneTex = _frameSceneTex;

            if (sceneTex == null || _buf.NativePtr == IntPtr.Zero ||
                sceneTex.Width <= GridSide || sceneTex.Height <= GridSide)
            {
                return;
            }

            MTLTexture stex = sceneTex.GetHandle(cbs);

            if (stex.NativePtr == IntPtr.Zero)
            {
                return;
            }

            int idx = (int)(_frame % Slots);
            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            for (int i = 0; i < Pixels; i++)
            {
                blit.CopyFromTexture(
                    stex, 0, 0,
                    new MTLOrigin
                    {
                        // Same texels as the present-time sample, so the two snapshots
                        // compare texel for texel.
                        x = (ulong)(sceneTex.Width / 2 + i % GridSide),
                        y = (ulong)(sceneTex.Height / 2 + i / GridSide),
                        z = 0,
                    },
                    new MTLSize { width = 1, height = 1, depth = 1 },
                    _buf, (ulong)(((2 * Slots + idx) * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
            }

            _frameAfterSampled = true;

            // The same texels again, through the sampler hardware. Back to back with the
            // blit-engine copies above, same stream position: the two decoders agree unless
            // the compression metadata is wrong.
            if (SamplerPathProbe.Ready)
            {
                SamplerPathProbe.Read(cbs, stex, (ulong)sceneTex.Width, (ulong)sceneTex.Height,
                    _buf, 3 * Slots * Pixels * BytesPerPixel + idx * Pixels * 16);
                _frameSamplerSampled = true;
                _frameInputFmt = sceneTex.MtlFormat;

                SamplerPathProbe.ReadSampled(cbs, stex, _buf,
                    3 * Slots * Pixels * BytesPerPixel + (Slots + idx) * Pixels * 16);
            }
        }

        private static bool _frameAfterSampled;
        private static bool _frameSamplerSampled;
        private static MTLPixelFormat _frameInputFmt;
        private static long _sampVsReadFlat, _sampSameFlat, _sampVsReadNormal, _sampSameNormal;
        private static long _twoPathDisagreeFlat, _twoPathAgreeFlat, _twoPathDisagreeNormal, _twoPathAgreeNormal;
        private static long _witFlatWhiteAfter, _witFlatPictureAfter, _witNormalWhiteAfter, _witNormalPictureAfter;
        private static double _witFlatEqSum; private static long _witFlatEqN;

        private static long _inputGen = -1, _writerGen = -1;
        private static long _genSwapFlat, _genSwapNormal, _genSameFlat, _genSameNormal;

        private static long _inputSerial = -1, _writerSerial = -1;
        private static long _matchFlat, _matchNormal, _mismatchFlat, _mismatchNormal;
        private static readonly Dictionary<string, (long Flat, long Normal)> _imgStats = new();
        private static string _frameRaster;

        /// <summary>
        /// The blit's position.w is a full projection row: dot(attr.xyz, c3[5].xyz) +
        /// c3[5].w, from guest data whose observed range spans thousands. Both backends run
        /// the same arithmetic, so this computes w on the CPU exactly as the vertex shader
        /// does, for all four vertices, against row 5 of every uniform slot bound at that
        /// draw - the real c3 identifies itself by sitting near 1 on normal frames, and the
        /// question is whether it collapses toward zero on exactly the flat ones. A w near
        /// zero at a vertex makes perspective interpolation of the UV explode across the
        /// primitive while every input value stays "correct".
        /// </summary>
        private static readonly float[] _frameVerts = new float[12];
        private static readonly float[] _frameRowW = new float[MaxCbSlots];
        private static readonly bool[] _frameRowSeen = new bool[MaxCbSlots];
        private static readonly double[] _wFlatSum = new double[MaxCbSlots];
        private static readonly double[] _wNormalSum = new double[MaxCbSlots];
        private static readonly float[] _wFlatMin = new float[MaxCbSlots];
        private static readonly float[] _wNormalMin = new float[MaxCbSlots];
        private static readonly long[] _wFlatN = new long[MaxCbSlots];
        private static readonly long[] _wNormalN = new long[MaxCbSlots];
        private static bool _wInit;

        public static unsafe void NoteBlitRow(int slot, IntPtr contents, int offset)
        {
            if (!Enabled || contents == IntPtr.Zero || (uint)slot >= MaxCbSlots)
            {
                return;
            }

            float* r = (float*)((byte*)contents + offset + 80);   // c3->data[5]
            float wmin = float.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                float w = _frameVerts[i * 3] * r[0] + _frameVerts[i * 3 + 1] * r[1] +
                          _frameVerts[i * 3 + 2] * r[2] + r[3];

                if (Math.Abs(w) < Math.Abs(wmin))
                {
                    wmin = w;
                }
            }

            _frameRowW[slot] = wmin;
            _frameRowSeen[slot] = true;
        }
        private static readonly Dictionary<string, (long Flat, long Normal)> _rasterStats = new();
        private static string _frameAttrib;
        private static readonly Dictionary<string, (long Flat, long Normal)> _attribStats = new();
        private static string _frameIndices;
        private static readonly Dictionary<string, (long Flat, long Normal)> _indexStats = new();
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

        // Classification happens only when the sampler's own fence reports signalled, and a
        // fenced arm freezes at a fixed frame count with nothing dropped - which points here
        // rather than at the ring. Counted so the next fenced arm says whether the fence
        // stopped signalling or the slots stopped being valid.
        private static long _fenceAsked, _fenceReady;

        // Mean luma of the sampled grid, split by outcome. Without it a run whose
        // picture went black reports zero flat frames and reads as a fix - which is
        // exactly how a "this build might suppress it" result was once produced.
        private static long _magentaFrames;

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

        private static CommandBufferPool _pool;

        public static void AttachPool(CommandBufferPool pool)
        {
            _pool = pool;
        }

        public static void Init(MTLDevice device)
        {
            if (!Enabled)
            {
                return;
            }

            _buf = device.NewBuffer((ulong)(4 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16), MTLResourceOptions.ResourceStorageModeShared);
            SamplerPathProbe.Initialize(device);

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

        public static void NoteAttachment(Texture target, bool clearLoad = false)
        {
            if (!Enabled || target == null)
            {
                return;
            }

            _attachedThisFrame.Add(target.CanonicalPtr);
            _lastAttachmentFrame[target.CanonicalPtr] = _frame;

            // How the game's final RT gets its content: log the pass that binds it - was
            // it cleared, how many draws followed (filled in at pass end), which program.
            if (target.CanonicalPtr == _frameGameRtRoot || target.CanonicalPtr == _prevGameRtRoot)
            {
                _gameRtPassOpen = true;
                _gameRtPassDrawStart = _drawsSeen;
                _gameRtPassClear = clearLoad;
                if (++_gameRtPassLogs % 300 == 1)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"game RT pass BEGIN frame={_frame} clearLoad={clearLoad}");
                }
            }

            if (target.CanonicalPtr == _compositeInputRoot)
            {
                _writerSerial = target.Serial;
                _writerGen = GenOf(target.CanonicalPtr);
            }

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
        /// <summary>
        /// The pair that matters. Every out-of-pass witness runs after the writer of the
        /// blit's input has completed; only the blit's own fragment stage sees white. The
        /// global out-of-order counter (0.54/frame, uncorrelated) cannot resolve whether
        /// THIS writer's command buffer and THIS blit's command buffer commit in the order
        /// they were rented - so both identities are recorded here, per frame, and the
        /// pair's rent-vs-commit order is split by outcome at classification.
        /// </summary>
        private static int _frameWriterCb = -1;
        private static long _frameWriterRent = -1;
        private static int _frameBlitCb = -1;
        private static long _frameBlitRent = -1;
        private static IntPtr _frameBlitInputRoot;

        // Writers happen BEFORE the blit in the frame, so the writer cannot be matched
        // against the blit's input at the time it draws - the first version keyed on a
        // root that was not yet known and paired 19 frames of 11,399. Record every
        // attachment root's most recent writer cb instead, and look it up at the blit.
        private static readonly Dictionary<IntPtr, (int Cb, long Rent)> _lastWriterByRoot = new();

        public static void NoteWriterCb(IntPtr root, int cbIndex, long rentSeq)
        {
            if (Enabled && root != IntPtr.Zero)
            {
                _lastWriterByRoot[root] = (cbIndex, rentSeq);
            }
        }

        public static void NoteBlitCb(IntPtr inputRoot, int cbIndex, long rentSeq)
        {
            if (!Enabled)
            {
                return;
            }

            _frameBlitInputRoot = inputRoot;
            _frameBlitCb = cbIndex;
            _frameBlitRent = rentSeq;

            // The capture's shape: Render Encoder 0 (HUD, attachment X) then the RCAS blit
            // reading Y, and X != Y on the white frame. _lastAttachmentRoot is X here.
            _frameBlitReadsPrevAttachment = inputRoot == _lastAttachmentRoot;
            _frameBlitPairKnown = _lastAttachmentRoot != IntPtr.Zero;

            if (_lastWriterByRoot.TryGetValue(inputRoot, out (int Cb, long Rent) w))
            {
                _frameWriterCb = w.Cb;
                _frameWriterRent = w.Rent;
            }
        }

        private static long _pairSameCbFlat, _pairSameCbNormal;
        private static long _pairOrderedFlat, _pairOrderedNormal;
        private static long _pairInvertedFlat, _pairInvertedNormal;
        private static long _pairUncommittedFlat, _pairUncommittedNormal;

        private static IntPtr _lastAttachmentRoot;
        private static bool _frameBlitReadsPrevAttachment, _frameBlitPairKnown;
        private static long _blitPrevMatchFlat, _blitPrevMatchNormal, _blitPrevMismatchFlat, _blitPrevMismatchNormal;

        public static void NoteAttachmentDraw(IntPtr root, string program)
        {
            _drawsSeen++;
            _lastAttachmentRoot = root;
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
            // Copies and uploads are writers too; without them the present source's
            // writer list held only draws, and the capture shows the frame's picture and
            // the presented white living in two different textures.
            if (Enabled && target != null) { NoteAttachmentDraw(target.CanonicalPtr, "upload"); }
            NoteWriteOrdering(target.CanonicalPtr, "upload");

            Note(target, isCopy: false);
        }

        public static void NoteCopyIn(Texture target)
        {
            // Copies and uploads are writers too; without them the present source's
            // writer list held only draws, and the capture shows the frame's picture and
            // the presented white living in two different textures.
            if (Enabled && target != null) { NoteAttachmentDraw(target.CanonicalPtr, "copy"); }
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

                if (slot.Valid)
                {
                    _fenceAsked++;
                }

                if (slot.Valid && slot.Fence.IsSignaled())
                {
                    _fenceReady++;
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
                mine.Indices = _frameIndices;
                mine.Attrib = _frameAttrib;
                mine.Raster = _frameRaster;
                mine.WriterCb = _frameWriterCb;
                mine.WriterRent = _frameWriterRent;
                mine.BlitCb = _frameBlitCb;
                mine.BlitRent = _frameBlitRent;
                mine.PsoFresh = _framePsoFresh;
                mine.ImgWriters = _frameImgWriters.Count == 0 ? "none" : string.Join(",", _frameImgWriters);
                mine.InSerial = _inputSerial;
                mine.WrSerial = _writerSerial;
                mine.AfterSampled = _frameAfterSampled;
                mine.SamplerSampled = _frameSamplerSampled;
                mine.InputFmt = _frameInputFmt;
                mine.InGen = _inputGen;
                mine.WrGen = _writerGen;
                mine.RowW ??= new float[MaxCbSlots];
                mine.RowSeen ??= new bool[MaxCbSlots];
                Array.Copy(_frameRowW, mine.RowW, MaxCbSlots);
                Array.Copy(_frameRowSeen, mine.RowSeen, MaxCbSlots);
                mine.OutOfOrder = _frameOutOfOrder;
                mine.ArgPtr = _frameArgPtr;
                mine.PresentMatchKnown = _frameLastFullResRt != IntPtr.Zero;
                mine.PresentMatchesLastRt = _frameLastFullResRt == src.CanonicalPtr;
                mine.Rgba8Known = _frameLastRgba8Rt != IntPtr.Zero;
                mine.Rgba8Match = _frameLastRgba8Rt == src.CanonicalPtr;
                mine.BlitPrevKnown = _frameBlitPairKnown;
                mine.SrcWasRecentDst = _frameSrcWasRecentDst;
                // Census the game's final RGBA8(sRGB) render target - the texture that is
                // white when the frame is white - not present's shadow of it.
                // The game's final RT was drawn LAST frame (lastAttachFrame == nowFrame - 1,
                // measured), so this frame's writer table never holds its writers. Use the
                // list snapshotted at the previous present.
                mine.SceneWriters = _prevFrameGameRtWriters;
                if (_gameRtPassOpen && ++_gameRtEndLogs % 300 == 1)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"game RT pass: clear={_gameRtPassClear} drawsSince={_drawsSeen - _gameRtPassDrawStart}");
                }
                _gameRtPassOpen = false;
                _prevFrameGameRtWriters = _frameGameRtRoot != IntPtr.Zero && _pendingWriterCount.TryGetValue(_frameGameRtRoot, out int swc) && swc > 0
                    ? string.Join(",", _pendingWriters[_frameGameRtRoot], 0, Math.Min(swc, MaxWriters))
                    : "(none-this-frame)";
                mine.RtWriters = _frameLastFullResRt != IntPtr.Zero && _pendingWriterCount.TryGetValue(_frameLastFullResRt, out int rwc) && rwc > 0
                    ? string.Join(",", _pendingWriters[_frameLastFullResRt], 0, Math.Min(rwc, MaxWriters))
                    : "(none)";
                mine.Triple = $"src{(_presentSrcRoot == _frameLastFullResRt ? "==" : "!=")}lastRT src{(_presentSrcRoot == _presentDstRoot ? "==" : "!=")}dst {_presentSrcW}x{_presentSrcH} srcSerial{(_presentSrcSerial == _prevPresentSrcSerial ? "SAME" : "new")}";
                mine.BlitPrevMatch = _frameBlitReadsPrevAttachment;
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

                // Region E: the same 25-point grid from the frame's last full-resolution
                // colour render target - the texture the capture showed holding the
                // correct picture while present read white from another.
                mine.RtSampled = false;
                mine.ReplaceViews = _frameReplaceViews;
                mine.NewTextures = _frameNewTextures;
                mine.ModifiedBy = _frameModifiedBy.Length == 0 ? "(none)" : _frameModifiedBy;
                Texture rtTex = _frameGameRtTex ?? _frameLastFullResTex;
                if (rtTex != null && rtTex.CanonicalPtr != src.CanonicalPtr)
                {
                    MTLTexture rt = rtTex.GetHandle(cbs);
                    if (rt.NativePtr != IntPtr.Zero)
                    {
                        // Sample through a BGRA-normalising path? RG11B10 raw bytes are not
                        // BGRA; decode with the sampler probe instead: use the compute read.
                        if (SamplerPathProbe.Ready)
                        {
                            SamplerPathProbe.Read(cbs, rt, (ulong)rtTex.Width, (ulong)rtTex.Height, _buf,
                                3 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16 - Slots * Pixels * 16 + idx * Pixels * 16);
                            mine.RtSampled = true;
                        }
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
            // kept across frames; see the note above - clearing here starved the serial probe
            _compositeSeq = -1;
            _frameSeq = 0;
            _frameInputWrittenAfter = false;
            _frameResidency = -1;
            _frameCompositeDraws = 0;
            _frameVertexDistinct = -1;
            _frameVertexStride = -1;
            _frameDraw = (-1, -1, -1, -1);
            _frameIndices = null;
            _frameAttrib = null;
            _frameRaster = null;
            _frameWriterCb = -1;
            _frameWriterRent = -1;
            _frameBlitCb = -1;
            _frameBlitRent = -1;
            _frameBlitInputRoot = IntPtr.Zero;
            _framePsoFresh = false;
            _frameImgWriters.Clear();
            _inputSerial = -1;
            _writerSerial = -1;
            _inputGen = -1;
            _frameAfterSampled = false;
            _frameSamplerSampled = false;
            _afterBlitArmed = false;
            _writerGen = -1;
            // _compositeInputRoot deliberately NOT reset: the root is stable across frames
            // and resetting it at present starved the writer-serial trigger to 42 of 7,799.
            Array.Clear(_frameRowSeen);
            _frameOutOfOrder = 0;
            _frameArgPtr = IntPtr.Zero;
            _frameLastFullResRt = IntPtr.Zero;
            _frameReplaceViews = 0;
            _frameNewTextures = 0;
            _frameModifiedBy = "";
            _frameLastRgba8Rt = IntPtr.Zero;
            _frameSceneSourceRoot = IntPtr.Zero;
            _frameBlitPairKnown = false;
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
            int magenta = 0;
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

                // Canary detection: magenta has luma ~128 and would classify as normal, so
                // it is counted directly. px[1] is green in this layout.
                if (px[0] > 200 && px[2] > 200 && px[1] < 60)
                {
                    magenta++;
                }
            }

            if (magenta >= SaturatedNeeded)
            {
                _magentaFrames++;
            }

            bool flat = saturated >= SaturatedNeeded;

            if (slot.AfterSampled && slot.SamplerSampled)
            {
                uint* rawC = (uint*)((byte*)_buf.Contents + (2 * Slots + index) * Pixels * BytesPerPixel);
                float* fD = (float*)((byte*)_buf.Contents + 3 * Slots * Pixels * BytesPerPixel + index * Pixels * 16);
                int disagree = 0;

                for (int i = 0; i < Pixels; i++)
                {
                    (float r, float g, float b) = SamplerPathProbe.DecodeRaw(rawC[i], slot.InputFmt);

                    if (!float.IsNaN(r) &&
                        (Math.Abs(r - fD[i * 4]) > 0.02f || Math.Abs(g - fD[i * 4 + 1]) > 0.02f || Math.Abs(b - fD[i * 4 + 2]) > 0.02f))
                    {
                        disagree++;
                    }
                }

                // Third path: the sampler unit against the read path, float against float.
                float* fS = (float*)((byte*)_buf.Contents + 3 * Slots * Pixels * BytesPerPixel + (Slots + index) * Pixels * 16);
                int sampDisagree = 0;

                for (int i = 0; i < Pixels; i++)
                {
                    if (Math.Abs(fS[i * 4] - fD[i * 4]) > 0.02f ||
                        Math.Abs(fS[i * 4 + 1] - fD[i * 4 + 1]) > 0.02f ||
                        Math.Abs(fS[i * 4 + 2] - fD[i * 4 + 2]) > 0.02f)
                    {
                        sampDisagree++;
                    }
                }

                if (sampDisagree >= 3) { if (flat) { _sampVsReadFlat++; } else { _sampVsReadNormal++; } }
                else { if (flat) { _sampSameFlat++; } else { _sampSameNormal++; } }

                bool paths = disagree >= 3;
                if (flat) { if (paths) { _twoPathDisagreeFlat++; } else { _twoPathAgreeFlat++; } }
                else { if (paths) { _twoPathDisagreeNormal++; } else { _twoPathAgreeNormal++; } }
            }

            if (slot.AfterSampled && slot.InputSampled)
            {
                uint* qa = (uint*)((byte*)_buf.Contents + (2 * Slots + index) * Pixels * BytesPerPixel);
                uint* qp = (uint*)((byte*)_buf.Contents + (Slots + index) * Pixels * BytesPerPixel);
                int distinctAfter = 0;
                int eq = 0;

                for (int i = 0; i < Pixels; i++)
                {
                    bool seen = false;
                    for (int j = 0; j < i; j++) { if (qa[j] == qa[i]) { seen = true; break; } }
                    if (!seen) { distinctAfter++; }
                    if (qa[i] == qp[i]) { eq++; }
                }

                // Uniform after the blit = the GPU saw a flat input when the blit ran.
                bool afterUniform = distinctAfter <= 3;

                if (flat)
                {
                    if (afterUniform) { _witFlatWhiteAfter++; } else { _witFlatPictureAfter++; }
                    _witFlatEqSum += eq; _witFlatEqN++;
                }
                else
                {
                    if (afterUniform) { _witNormalWhiteAfter++; } else { _witNormalPictureAfter++; }
                }
            }

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

            if (slot.SceneWriters != null && (_sceneWriterStats.Count < 40 || _sceneWriterStats.ContainsKey(slot.SceneWriters)))
            {
                (long sf2, long sn2) = _sceneWriterStats.TryGetValue(slot.SceneWriters, out (long Flat, long Normal) sv2) ? (sv2.Flat, sv2.Normal) : (0L, 0L);
                _sceneWriterStats[slot.SceneWriters] = flat ? (sf2 + 1, sn2) : (sf2, sn2 + 1);
            }

            if (slot.RtWriters != null && (_rtWriterStats.Count < 40 || _rtWriterStats.ContainsKey(slot.RtWriters)))
            {
                (long rf, long rn) = _rtWriterStats.TryGetValue(slot.RtWriters, out (long Flat, long Normal) rv) ? (rv.Flat, rv.Normal) : (0L, 0L);
                _rtWriterStats[slot.RtWriters] = flat ? (rf + 1, rn) : (rf, rn + 1);
            }

            if (slot.ModifiedBy != null && (_modByStats.Count < 16 || _modByStats.ContainsKey(slot.ModifiedBy)))
            {
                (long mf, long mn) = _modByStats.TryGetValue(slot.ModifiedBy, out (long Flat, long Normal) mv) ? (mv.Flat, mv.Normal) : (0L, 0L);
                _modByStats[slot.ModifiedBy] = flat ? (mf + 1, mn) : (mf, mn + 1);
            }

            if (flat) { _rvFlat += slot.ReplaceViews; _ntFlat += slot.NewTextures; _topoFlatN++; }
            else { _rvNormal += slot.ReplaceViews; _ntNormal += slot.NewTextures; _topoNormalN++; }

            if (slot.RtSampled)
            {
                float* fE = (float*)((byte*)_buf.Contents + 3 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16 - Slots * Pixels * 16 + index * Pixels * 16);
                double rtLumaSum = 0; int rtSat = 0;
                for (int i = 0; i < Pixels; i++)
                {
                    // RG11B10 decoded floats are linear HDR; treat > 0.9 in all channels as white
                    float r = fE[i * 4], g = fE[i * 4 + 1], b = fE[i * 4 + 2];
                    double lum = (r + g + g + b) * 0.25;
                    rtLumaSum += lum;
                    if (r > 0.9f && g > 0.9f && b > 0.9f) { rtSat++; }
                }
                bool rtWhite = rtSat >= SaturatedNeeded;
                bool srcWhite = flat;
                if (srcWhite && !rtWhite) { if (flat) { _rtPicSrcWhiteFlat++; } else { _rtPicSrcWhiteNormal++; } }
                else if (!srcWhite && !rtWhite) { if (flat) { _bothPicFlat++; } else { _bothPicNormal++; } }
                else if (srcWhite && rtWhite) { if (flat) { _bothWhiteFlat++; } else { _bothWhiteNormal++; } }
                else { if (flat) { _rtWhiteSrcPicFlat++; } else { _rtWhiteSrcPicNormal++; } }
            }

            if (slot.SrcWasRecentDst) { if (flat) { _srcWasRecentDstFlat++; } else { _srcWasRecentDstNormal++; } }
            else { if (flat) { _srcNotDstFlat++; } else { _srcNotDstNormal++; } }

            if (slot.Triple != null && _tripleStats.Count < 32)
            {
                (long tf, long tn) = _tripleStats.TryGetValue(slot.Triple, out (long Flat, long Normal) tv) ? (tv.Flat, tv.Normal) : (0L, 0L);
                _tripleStats[slot.Triple] = flat ? (tf + 1, tn) : (tf, tn + 1);
            }

            if (slot.BlitPrevKnown)
            {
                if (slot.BlitPrevMatch) { if (flat) { _blitPrevMatchFlat++; } else { _blitPrevMatchNormal++; } }
                else { if (flat) { _blitPrevMismatchFlat++; } else { _blitPrevMismatchNormal++; } }
            }

            if (!slot.Rgba8Known) { if (flat) { _rgbaNoneFlat++; } else { _rgbaNoneNormal++; } }
            else if (slot.Rgba8Match) { if (flat) { _rgbaMatchFlat++; } else { _rgbaMatchNormal++; } }
            else { if (flat) { _rgbaMismFlat++; } else { _rgbaMismNormal++; } }

            if (slot.PresentMatchKnown)
            {
                if (slot.PresentMatchesLastRt) { if (flat) { _presentMatchFlat++; } else { _presentMatchNormal++; } }
                else { if (flat) { _presentMismatchFlat++; } else { _presentMismatchNormal++; } }
            }

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

            if (flat) { _flatOooSum += slot.OutOfOrder; _flatOooN++; }
            else { _normalOooSum += slot.OutOfOrder; _normalOooN++; }

            if (slot.RowSeen != null)
            {
                if (!_wInit)
                {
                    _wInit = true;
                    for (int c = 0; c < MaxCbSlots; c++) { _wFlatMin[c] = _wNormalMin[c] = float.MaxValue; }
                }

                for (int c = 0; c < MaxCbSlots; c++)
                {
                    if (!slot.RowSeen[c]) { continue; }
                    float w = slot.RowW[c];
                    if (flat)
                    {
                        _wFlatSum[c] += w; _wFlatN[c]++;
                        if (Math.Abs(w) < Math.Abs(_wFlatMin[c])) { _wFlatMin[c] = w; }
                    }
                    else
                    {
                        _wNormalSum[c] += w; _wNormalN[c]++;
                        if (Math.Abs(w) < Math.Abs(_wNormalMin[c])) { _wNormalMin[c] = w; }
                    }
                }
            }

            if (slot.InGen >= 0 && slot.WrGen >= 0)
            {
                if (slot.InGen != slot.WrGen) { if (flat) { _genSwapFlat++; } else { _genSwapNormal++; } }
                else { if (flat) { _genSameFlat++; } else { _genSameNormal++; } }
            }

            if (slot.InSerial >= 0 && slot.WrSerial >= 0)
            {
                bool match = slot.InSerial == slot.WrSerial;
                if (flat) { if (match) { _matchFlat++; } else { _mismatchFlat++; } }
                else { if (match) { _matchNormal++; } else { _mismatchNormal++; } }
            }

            if (slot.ImgWriters != null && _imgStats.Count < 32)
            {
                (long gf, long gn) = _imgStats.TryGetValue(slot.ImgWriters, out (long Flat, long Normal) gv) ? (gv.Flat, gv.Normal) : (0L, 0L);
                _imgStats[slot.ImgWriters] = flat ? (gf + 1, gn) : (gf, gn + 1);
            }

            if (slot.Raster != null)
            {
                if (slot.PsoFresh) { if (flat) { _freshFlat++; } else { _freshNormal++; } }
                else { if (flat) { _staleFlat++; } else { _staleNormal++; } }
            }

            if (slot.WriterCb >= 0 && slot.BlitCb >= 0)
            {
                if (slot.WriterCb == slot.BlitCb && slot.WriterRent == slot.BlitRent)
                {
                    if (flat) { _pairSameCbFlat++; } else { _pairSameCbNormal++; }
                }
                else
                {
                    // Commit order of the pair, read at classification time (several
                    // frames later, both long committed). Rent order says which SHOULD
                    // be first; commit order says which WAS.
                    long wc = _pool.CommitSeqOf(slot.WriterCb);
                    long bc = _pool.CommitSeqOf(slot.BlitCb);
                    bool writerFirstByRent = slot.WriterRent < slot.BlitRent;

                    if (wc == 0 || bc == 0)
                    {
                        if (flat) { _pairUncommittedFlat++; } else { _pairUncommittedNormal++; }
                    }
                    else if ((wc < bc) == writerFirstByRent)
                    {
                        if (flat) { _pairOrderedFlat++; } else { _pairOrderedNormal++; }
                    }
                    else
                    {
                        if (flat) { _pairInvertedFlat++; } else { _pairInvertedNormal++; }
                    }
                }
            }

            if (slot.Raster != null && _rasterStats.Count < 32)
            {
                (long rf, long rn) = _rasterStats.TryGetValue(slot.Raster, out (long Flat, long Normal) rv) ? (rv.Flat, rv.Normal) : (0L, 0L);
                _rasterStats[slot.Raster] = flat ? (rf + 1, rn) : (rf, rn + 1);
            }

            if (slot.Attrib != null && _attribStats.Count < 32)
            {
                (long af2, long an2) = _attribStats.TryGetValue(slot.Attrib, out (long Flat, long Normal) av2) ? (av2.Flat, av2.Normal) : (0L, 0L);
                _attribStats[slot.Attrib] = flat ? (af2 + 1, an2) : (af2, an2 + 1);
            }

            if (slot.Indices != null && _indexStats.Count < 32)
            {
                (long jf, long jn) = _indexStats.TryGetValue(slot.Indices, out (long Flat, long Normal) jv) ? (jv.Flat, jv.Normal) : (0L, 0L);
                _indexStats[slot.Indices] = flat ? (jf + 1, jn) : (jf, jn + 1);
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

            for (int c = 0; c < MaxCbSlots; c++)
            {
                if (_wFlatN[c] == 0 && _wNormalN[c] == 0) { continue; }
                sb.Append($"\n  blit w[slot{c}]: flat mean {(_wFlatN[c] > 0 ? _wFlatSum[c] / _wFlatN[c] : 0):G4} min {(_wFlatN[c] > 0 ? _wFlatMin[c] : 0):G4} n={_wFlatN[c]}  normal mean {(_wNormalN[c] > 0 ? _wNormalSum[c] / _wNormalN[c] : 0):G4} min {(_wNormalN[c] > 0 ? _wNormalMin[c] : 0):G4} n={_wNormalN[c]}");
            }

            sb.Append($"\n  WRITER/BLIT PAIR: same-cb flat {_pairSameCbFlat} normal {_pairSameCbNormal} | ordered flat {_pairOrderedFlat} normal {_pairOrderedNormal} | INVERTED flat {_pairInvertedFlat} normal {_pairInvertedNormal} | uncommitted flat {_pairUncommittedFlat} normal {_pairUncommittedNormal}");
            sb.Append($"\n  SAMPLER-UNIT vs read-path disagree: flat {_sampVsReadFlat}/{_sampVsReadFlat + _sampSameFlat}, normal {_sampVsReadNormal}/{_sampVsReadNormal + _sampSameNormal}");
            sb.Append($"\n  TWO-PATH sampler-vs-blit disagree: flat {_twoPathDisagreeFlat}/{_twoPathDisagreeFlat + _twoPathAgreeFlat}, normal {_twoPathDisagreeNormal}/{_twoPathDisagreeNormal + _twoPathAgreeNormal}");
            sb.Append($"\n  MAGENTA canary frames: {_magentaFrames}");
            sb.Append($"\n  blit PSO fresh-this-frame: flat {_freshFlat}/{_freshFlat + _staleFlat}, normal {_freshNormal}/{_freshNormal + _staleNormal}");
            sb.Append($"\n  WITNESS input-after-blit: flat uniform {_witFlatWhiteAfter} picture {_witFlatPictureAfter} (eq-with-present {(_witFlatEqN > 0 ? _witFlatEqSum / _witFlatEqN : 0):F1}/25), normal uniform {_witNormalWhiteAfter} picture {_witNormalPictureAfter}");
            sb.Append($"\n  handle gen swap between write and read: flat {_genSwapFlat}/{_genSwapFlat + _genSameFlat}, normal {_genSwapNormal}/{_genSwapNormal + _genSameNormal}");
            sb.Append($"\n  blit serial: flat match {_matchFlat} mismatch {_mismatchFlat}, normal match {_matchNormal} mismatch {_mismatchNormal}");

            foreach (KeyValuePair<string, (long Flat, long Normal)> g2 in _imgStats)
            {
                sb.Append($"\n  image-on-presented [{g2.Key}]: flat {g2.Value.Flat}, normal {g2.Value.Normal}");
            }

            foreach (KeyValuePair<string, (long Flat, long Normal)> r2 in _rasterStats)
            {
                sb.Append($"\n  blit raster {r2.Key}: flat {r2.Value.Flat}, normal {r2.Value.Normal}");
            }

            foreach (KeyValuePair<string, (long Flat, long Normal)> a2 in _attribStats)
            {
                sb.Append($"\n  vertex attrib0 {a2.Key}: flat {a2.Value.Flat}, normal {a2.Value.Normal}");
            }

            foreach (KeyValuePair<string, (long Flat, long Normal)> j in _indexStats)
            {
                sb.Append($"\n  blit indices [{j.Key}]: flat {j.Value.Flat}, normal {j.Value.Normal}");
            }

            foreach (KeyValuePair<string, (long Flat, long Normal)> d in _drawParamStats)
            {
                sb.Append($"\n  blit draw {d.Key}: flat {d.Value.Flat}, normal {d.Value.Normal}");
            }

            sb.Append($" | blit stride: flat {(_flatStrideN > 0 ? _flatStrideSum / _flatStrideN : 0):F1} [{(_flatStrideN > 0 ? _flatStrideMin : 0)},{(_flatStrideN > 0 ? _flatStrideMax : 0)}] over {_flatStrideN}, normal {(_normalStrideN > 0 ? _normalStrideSum / _normalStrideN : 0):F1} over {_normalStrideN}");
            sb.Append($" | blit vertex spread (distinct of 4): flat {(_flatVertN > 0 ? _flatVertSum / _flatVertN : 0):F2} over {_flatVertN}, normal {(_normalVertN > 0 ? _normalVertSum / _normalVertN : 0):F2} over {_normalVertN}");
            sb.Append($" | composite draws/frame: flat {(_flatDrawN > 0 ? _flatDrawSum / _flatDrawN : 0):F2}, normal {(_normalDrawN > 0 ? _normalDrawSum / _normalDrawN : 0):F2}");
            sb.Append($" | sampler fence: asked {_fenceAsked}, signalled {_fenceReady}");
            {
                List<KeyValuePair<string, (long Flat, long Normal)>> sw = new(_sceneWriterStats);
                sw.Sort((x, y) => (y.Value.Flat + y.Value.Normal).CompareTo(x.Value.Flat + x.Value.Normal));
                for (int i = 0; i < sw.Count && i < 10; i++)
                {
                    long tot = sw[i].Value.Flat + sw[i].Value.Normal;
                    sb.Append($"\n  SCENE-800 writers: flat {sw[i].Value.Flat,6} / {tot,6} = {(tot > 0 ? 100.0 * sw[i].Value.Flat / tot : 0),5:F1}%  {sw[i].Key}");
                }
            }
            {
                List<KeyValuePair<string, (long Flat, long Normal)>> rw = new(_rtWriterStats);
                rw.Sort((x, y) => (y.Value.Flat + y.Value.Normal).CompareTo(x.Value.Flat + x.Value.Normal));
                for (int i = 0; i < rw.Count && i < 8; i++)
                {
                    long tot = rw[i].Value.Flat + rw[i].Value.Normal;
                    sb.Append($"\n  LAST-RT writers: flat {rw[i].Value.Flat,6} / {tot,6} = {(tot > 0 ? 100.0 * rw[i].Value.Flat / tot : 0),5:F1}%  {rw[i].Key}");
                }
            }
            foreach (KeyValuePair<string, (long Flat, long Normal)> mb in _modByStats)
            {
                sb.Append($"\n  PRESENT-RANGE modified by [{mb.Key}]: flat {mb.Value.Flat} normal {mb.Value.Normal}");
            }
            sb.Append($"\n  TOPOLOGY/frame: replaceView flat {(_topoFlatN > 0 ? _rvFlat / _topoFlatN : 0):F3} normal {(_topoNormalN > 0 ? _rvNormal / _topoNormalN : 0):F3} | newTexture flat {(_topoFlatN > 0 ? _ntFlat / _topoFlatN : 0):F3} normal {(_topoNormalN > 0 ? _ntNormal / _topoNormalN : 0):F3}");
            sb.Append($"\n  RT-vs-SRC at present: [RT picture, SRC white] flat {_rtPicSrcWhiteFlat} normal {_rtPicSrcWhiteNormal} | [both picture] flat {_bothPicFlat} normal {_bothPicNormal} | [both white] flat {_bothWhiteFlat} normal {_bothWhiteNormal} | [RT white, SRC pic] flat {_rtWhiteSrcPicFlat} normal {_rtWhiteSrcPicNormal}");
            sb.Append($"\n  PRESENT src is a recent drawable: yes flat {_srcWasRecentDstFlat} normal {_srcWasRecentDstNormal} | no flat {_srcNotDstFlat} normal {_srcNotDstNormal}");
            foreach (KeyValuePair<string, (long Flat, long Normal)> t3 in _tripleStats)
            {
                sb.Append($"\n  PRESENT {t3.Key}: flat {t3.Value.Flat}, normal {t3.Value.Normal}");
            }
            sb.Append($" | BLIT reads the pass-before-it's attachment: match flat {_blitPrevMatchFlat} normal {_blitPrevMatchNormal} | MISMATCH flat {_blitPrevMismatchFlat} normal {_blitPrevMismatchNormal}");
            sb.Append($" | PRESENT src == last 1080p RGBA8 RT: match flat {_rgbaMatchFlat} normal {_rgbaMatchNormal} | mismatch flat {_rgbaMismFlat} normal {_rgbaMismNormal} | no-rgba8-rt flat {_rgbaNoneFlat} normal {_rgbaNoneNormal}");
            sb.Append($" | PRESENT src == last full-res RT: match flat {_presentMatchFlat} normal {_presentMatchNormal} | MISMATCH flat {_presentMismatchFlat} normal {_presentMismatchNormal}");
            sb.Append($" | argbuf overwritten by frame end: flat {_flatArgMismatch}/{_flatArgChecked}, normal {_normalArgMismatch}/{_normalArgChecked}");
            sb.Append($" | out-of-order commits/frame: flat {(_flatOooN > 0 ? _flatOooSum / _flatOooN : 0):F2}, normal {(_normalOooN > 0 ? _normalOooSum / _normalOooN : 0):F2}");
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
