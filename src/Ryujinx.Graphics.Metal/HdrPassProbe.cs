using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Per frame record of the passes that write the full resolution stages of the
    /// scene chain, so a white frame can be told apart from a good one by what
    /// actually ran.
    ///
    /// The chain sweep narrowed the fault to one stage: the 1600x896 scene colour is
    /// never uniform on a white frame, while the 1920x1080 RG11B10Float target above
    /// it is uniform on essentially every one. This records, for each full resolution
    /// target, how many passes wrote it, how many draws those passes contained and
    /// how many loaded with a clear - which separates the three ways that stage can
    /// come out uniform: no pass ran at all, a pass ran but drew nothing (leaving the
    /// clear), or a pass drew and produced white anyway.
    ///
    /// Bookkeeping only. Nothing here touches a Metal object or ends a pass, so it
    /// cannot move the race being measured.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class HdrPassProbe
    {
        public static bool Enabled => PresentProbe.Enabled;

        // Matches PresentProbe's ring so a frame classified two presents late can still
        // be described.
        private const int Slots = 4;
        // A frame touches more than sixteen targets, and the cap silently dropped the rest -
        // which is how the sRGB view of the present surface came to look as though nothing
        // ever wrote it. Raised, and what still gets dropped is now reported.
        private const int MaxTargets = 64;

        // Below this the target is a shadow map, a luminance reduction or a UI atlas,
        // not a full resolution stage of the chain under study.
        private const int MinWidth = 256;

        private struct Entry
        {
            public IntPtr Target;
            public int Width;
            public int Height;
            public MTLPixelFormat Format;
            public int Passes;
            public ulong Draws;
            public int Cleared;
        }

        private static readonly Entry[][] _slots = CreateSlots();
        private static readonly int[] _slotCount = new int[Slots];

        private static readonly Entry[] _pending = new Entry[MaxTargets];
        private static int _pendingCount;
        private static int _truncated;

        // The pass currently being encoded, so its draws can be attributed on end.
        private static int _openIndex = -1;

        // Per pass detail for the one stage the chain sweep implicated: the full
        // resolution HDR target. A white frame carries exactly one more pass on it than
        // a good frame while the draw total is unchanged, so the extra pass is empty -
        // this records where in the sequence it falls and what ended it.
        // The 1600x896 RG11B10Float stage takes about thirty passes carrying twenty-nine
        // draws - roughly one draw per pass, a post-process chain rather than the scene
        // geometry, which goes to the RGBA8 target at some 1500 draws. Twelve entries cut
        // that chain off two thirds of the way through, so the walk could never reach the
        // pass that matters. A flat frame also carries about nine more passes than its
        // predecessor at the same draw total, and those empty passes are only visible if
        // the whole sequence fits.
        private const int MaxWatched = 40;

        // Which RG11B10Float stage the per-pass content sampling follows. It was fixed at
        // the full resolution composite; now that the composite has been shown to read an
        // already-white 1600x896 scene texture, the same walk has to run one stage earlier,
        // and the stage after that if it comes to it. Exact width rather than a minimum, so
        // pointing it at 1600 does not also pick up 1920 and interleave two chains in one
        // sequence.
        private static readonly int WatchedWidth =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_WATCH_WIDTH"), out int w) && w > 0 ? w : 1920;

        private struct PassDetail
        {
            public ulong Draws;
            public PassEndReason Reason;
            public bool Cleared;
        }

        private static readonly PassDetail[][] _slotWatched = CreateWatched();
        private static readonly int[] _slotWatchedCount = new int[Slots];

        private static readonly PassDetail[] _pendingWatched = new PassDetail[MaxWatched];
        private static int _pendingWatchedCount;

        private static bool _openWatched;
        private static Texture _openWatchedTarget;

        // Per pass content of the watched target. Knowing only its state at present
        // time says the stage ends up white but not which pass made it so; sampling
        // after every pass that writes it names the draws responsible.
        private const int SamplePixels = 9;
        private const int SampleBytes = SamplePixels * 4;

        private static MTLBuffer _sampleBuf;
        private static MTLDevice _device;

        // What the watched stage READ. Every instrument so far measured the write side -
        // which target, how many passes, how many draws - and all of those come out
        // identical on a white frame. Identical draws producing white can only be a
        // difference in what they sampled, so this records the distinct textures bound
        // for the draws that write the watched target.
        private const int MaxSampled = 12;

        private static readonly IntPtr[][] _slotSampled = CreateSampled();
        private static readonly int[] _slotSampledCount = new int[Slots];

        private static readonly IntPtr[] _pendingSampled = new IntPtr[MaxSampled];
        private static readonly Texture[] _pendingSampledTex = new Texture[MaxSampled];
        private static int _pendingSampledCount;

        // Latched on first sight so the readback keeps sampling the same set even as
        // bindings churn; the set was already shown to be identical on white and good
        // frames, so what is left to compare is their contents.
        private static readonly Texture[] _watchTex = new Texture[MaxSampled];
        private static int _watchCount;

        private static IntPtr[][] CreateSampled()
        {
            IntPtr[][] slots = new IntPtr[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new IntPtr[MaxSampled];
            }

            return slots;
        }

        /// <summary>
        /// Whether this target is the stage the chain sweep implicated.
        /// </summary>
        /// <summary>
        /// Identity that survives views. A view carries its own MTLTexture pointer, so
        /// comparing handles or object references misses every write made through one.
        /// </summary>
        private static IntPtr RootOf(Texture t)
        {
            if (t == null)
            {
                return IntPtr.Zero;
            }

            // Canonical identity only. The old fallback chain (ViewRootPtr, else the
            // swizzled handle) hashed one resource to different values depending on which
            // path observed it, which is why several "nothing writes this" results in the
            // white-flash notes were false.
            return t.CanonicalPtr != IntPtr.Zero ? t.CanonicalPtr : t.GetHandle().NativePtr;
        }

        public static bool IsWatchedTarget(Texture target)
        {
            return target != null &&
                target.Width == WatchedWidth &&
                target.MtlFormat == MTLPixelFormat.RG11B10Float;
        }

        // The GPU memory dumps put the fault between the 1600x896 scene targets, none of
        // which ever hold the flat value, and the 1920x1080 composite, which does. This
        // names the shaders that span the two by reporting every distinct
        // sampled-size -> target-size pair, so the composite draw can be identified from
        // what it actually reads rather than from an assumption about it.
        private static readonly System.Collections.Generic.HashSet<string> _spanSeen = [];

        private static readonly System.Collections.Generic.HashSet<string> _copySeen = [];

        // Every identity a full resolution texture answers to. A view carries its own
        // MTLTexture pointer, and a write made through one is attributed to whichever
        // identity the bookkeeping happened to use - which is exactly how an earlier
        // "nothing ever writes the presented image" conclusion in these notes came about
        // and had to be retracted. Printing all three per texture makes the collision
        // visible instead of leaving two records that never line up.
        private static readonly System.Collections.Generic.HashSet<string> _identSeen = [];

        public static void NoteIdentity(string role, Texture t)
        {
            if (t == null || t.Width < 1900)
            {
                return;
            }

            string key = $"{role}:0x{t.GetHandle().NativePtr:X}";

            lock (_identSeen)
            {
                if (!_identSeen.Add(key))
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"hdrident {role} {t.Width}x{t.Height} {t.MtlFormat} " +
                $"handle=0x{t.GetHandle().NativePtr:X} canonical=0x{t.CanonicalPtr:X} " +
                $"viewRoot=0x{t.ViewRootPtr:X}");
        }

        // The composite's own draws, by shader. hdrspan mixed in the UI shaders that
        // write the sRGB view of the same storage; this counts only draws whose target is
        // the RG11B10Float composite, which is where the 96-97 draws land.
        private static readonly System.Collections.Generic.Dictionary<string, int> _compositeDraws = new();

        public static void NoteCompositeDraw(string program, Texture target)
        {
            if (program == null || !IsWatchedTarget(target))
            {
                return;
            }

            lock (_compositeDraws)
            {
                _compositeDraws.TryGetValue(program, out int n);
                _compositeDraws[program] = n + 1;

                if ((n + 1) % 2000 != 0)
                {
                    return;
                }
            }

            System.Text.StringBuilder sb = new();

            lock (_compositeDraws)
            {
                foreach (var kv in _compositeDraws)
                {
                    sb.Append($" {kv.Key}={kv.Value}");
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu, $"hdrcomposite{sb}");
        }

        public static void NoteCopy(int srcW, int srcH, string srcFmt, int dstW, int dstH, string dstFmt)
        {
            string key = $"{srcW}x{srcH}:{srcFmt}->{dstW}x{dstH}:{dstFmt}";

            lock (_copySeen)
            {
                if (!_copySeen.Add(key))
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu, $"hdrcopy {key}");
        }

        public static void NoteSpan(string program, int srcW, int srcH, int dstW, int dstH)
        {
            if (program == null || dstW < 900)
            {
                return;
            }

            lock (_spanSeen)
            {
                if (!_spanSeen.Add($"{program}:{srcW}x{srcH}->{dstW}x{dstH}"))
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"hdrspan program={program} samples {srcW}x{srcH} -> writes {dstW}x{dstH}");
        }

        public static void NoteSampled(IntPtr texture, TextureBase storage = null)
        {
            if (texture == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < _pendingSampledCount; i++)
            {
                if (_pendingSampled[i] == texture)
                {
                    return;
                }
            }

            if (_pendingSampledCount < MaxSampled)
            {
                _pendingSampledTex[_pendingSampledCount] = storage as Texture;
                _pendingSampled[_pendingSampledCount++] = texture;

                if (_watchCount < MaxSampled && storage is Texture t && t.Width >= 256)
                {
                    for (int i = 0; i < _watchCount; i++)
                    {
                        if (ReferenceEquals(_watchTex[i], t))
                        {
                            return;
                        }
                    }

                    _watchTex[_watchCount++] = t;
                }
            }
        }

        private static PassDetail[][] CreateWatched()
        {
            PassDetail[][] slots = new PassDetail[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new PassDetail[MaxWatched];
            }

            return slots;
        }

        private static Entry[][] CreateSlots()
        {
            Entry[][] slots = new Entry[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new Entry[MaxTargets];
            }

            return slots;
        }

        /// <summary>
        /// Called as a render pass descriptor is built, with its first colour attachment.
        /// </summary>
        /// <summary>
        /// Records every colour attachment of the pass, not just the first. Only tracking
        /// attachment 0 is why a good frame's present source appeared to have no writer:
        /// a pass that binds it at attachment 1 or above was invisible.
        /// </summary>
        public static void BeginPassAll(Texture[] targets, bool clearLoadAction)
        {
            BeginPass(targets.Length > 0 ? targets[0] : null, clearLoadAction);

            for (int i = 1; i < targets.Length; i++)
            {
                RecordTarget(targets[i], clearLoadAction);
            }
        }

        private static void RecordTarget(Texture target, bool clearLoadAction)
        {
            if (target == null || target.Width < MinWidth)
            {
                return;
            }

            IntPtr handle = RootOf(target);

            if (handle == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Target == handle)
                {
                    _pending[i].Passes++;
                    return;
                }
            }

            if (_pendingCount == MaxTargets)
            {
                return;
            }

            _pending[_pendingCount] = new Entry
            {
                Target = handle,
                Width = target.Width,
                Height = target.Height,
                Format = target.MtlFormat,
                Passes = 1,
            };

            _pendingCount++;
        }

        public static void BeginPass(Texture target, bool clearLoadAction)
        {
            _openIndex = -1;
            _openWatched = false;

            if (target == null || target.Width < MinWidth)
            {
                return;
            }

            if (IsWatchedTarget(target) && _pendingWatchedCount < MaxWatched)
            {
                _pendingWatched[_pendingWatchedCount] = new PassDetail { Cleared = clearLoadAction };
                _openWatched = true;
                _openWatchedTarget = target;
            }

            IntPtr handle = RootOf(target);

            if (handle == IntPtr.Zero)
            {
                return;
            }

            int index = -1;

            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Target == handle)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                if (_pendingCount == MaxTargets)
                {
                    _truncated++;

                    return;
                }

                index = _pendingCount++;

                _pending[index] = new Entry
                {
                    Target = handle,
                    Width = target.Width,
                    Height = target.Height,
                    Format = target.MtlFormat,
                };
            }

            _pending[index].Passes++;

            if (clearLoadAction)
            {
                _pending[index].Cleared++;
            }

            _openIndex = index;
        }

        /// <summary>
        /// Called when that pass ends, with the draws it contained.
        /// </summary>
        public static void Initialize(MTLDevice device)
        {
            _device = device;
            _inputBuf = device.NewBuffer(
                (ulong)(Slots * InputsPerFrame * InputBytes),
                MTLResourceOptions.ResourceStorageModeShared);

            _sampleBuf = device.NewBuffer(
                (ulong)(Slots * MaxWatched * SampleBytes),
                MTLResourceOptions.ResourceStorageModeShared);
        }

        /// <summary>
        /// Blits a 3x3 grid out of the watched target right after a pass that wrote it.
        /// The pass has already ended, so this only adds a blit encoder between passes.
        /// </summary>
        /// <summary>
        /// Samples the watched target just BEFORE a pass that writes it, so the sequence
        /// across a frame says which pass left it white.
        ///
        /// Must be called from a point that is allowed to switch encoders. An earlier
        /// version sampled from the pass-ended callback, where the render encoder has not
        /// been closed yet, and Metal asserted on the blit encoder it tried to open.
        /// </summary>
        /// <summary>
        /// The last watched target seen this frame, kept after the pass closes so the
        /// content can be sampled once more at present.
        ///
        /// The chain above samples before each pass, which reads as "after the previous
        /// one" for every pass but the last - and the last one's output is exactly what the
        /// next stage reads. Walking the 1600x896 stage produced eight "varied" entries and
        /// no verdict for that reason, while the shader reading this texture was measuring
        /// 254 in every channel. The missing sample is the whole answer.
        /// </summary>
        private static Texture _lastWatchedTarget;

        public static void SampleAtPresent(CommandBufferScoped cbs, int frameSlot)
        {
            if (_lastWatchedTarget == null)
            {
                return;
            }

            if (_pendingWatchedCount >= MaxWatched)
            {
                return;
            }

            SampleWatched(cbs, _lastWatchedTarget, frameSlot, _pendingWatchedCount);

            // Claim the slot so Commit carries it and DescribePassContent prints it as the
            // final entry. It belongs to no pass, hence the zero draws.
            _pendingWatched[_pendingWatchedCount] = new PassDetail();
            _pendingWatchedCount++;
            _lastWatchedTarget = null;
        }

        public static void SampleBeforePass(CommandBufferScoped cbs, Texture target, int frameSlot)
        {
            if (_sampleBuf.NativePtr == IntPtr.Zero || !IsWatchedTarget(target))
            {
                return;
            }

            _openWatchedTarget = target;
            _lastWatchedTarget = target;

            SampleWatched(cbs, target, frameSlot, _pendingWatchedCount);
        }

        private static void SampleWatched(CommandBufferScoped cbs, Texture target, int frameSlot, int passIndex)
        {
            if (_sampleBuf.NativePtr == IntPtr.Zero || target == null)
            {
                return;
            }

            _openWatchedTarget = target;

            if (passIndex >= MaxWatched)
            {
                return;
            }

            MTLTexture tex = _openWatchedTarget.GetHandle();

            if (tex.NativePtr == IntPtr.Zero)
            {
                return;
            }

            int w = _openWatchedTarget.Width;
            int h = _openWatchedTarget.Height;
            ulong baseOffset = (ulong)((frameSlot * MaxWatched + passIndex) * SampleBytes);

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            for (int gy = 0; gy < 3; gy++)
            {
                for (int gx = 0; gx < 3; gx++)
                {
                    blit.CopyFromTexture(
                        tex, 0, 0,
                        new MTLOrigin { x = (ulong)(w * (gx + 1) / 4), y = (ulong)(h * (gy + 1) / 4), z = 0 },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _sampleBuf,
                        baseOffset + (ulong)((gy * 3 + gx) * 4),
                        4, 4);
                }
            }
        }

        /// <summary>
        /// Which pass first left the watched target uniform, read back once the frame is
        /// long finished. RG11B10Float packs into 32 bits, so equal words mean equal
        /// texels without needing to decode the format.
        /// </summary>
        public static unsafe string DescribePassContent(int slot)
        {
            if (_sampleBuf.NativePtr == IntPtr.Zero)
            {
                return "n/a";
            }

            int count = _slotWatchedCount[slot];
            StringBuilder sb = new();
            uint* words = (uint*)_sampleBuf.Contents;

            for (int p = 0; p < count && p < MaxWatched; p++)
            {
                uint* px = words + (slot * MaxWatched + p) * SamplePixels;
                bool uniform = true;

                for (int i = 1; i < SamplePixels; i++)
                {
                    if (px[i] != px[0])
                    {
                        uniform = false;
                        break;
                    }
                }

                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }

                // The draw count belongs beside the content: a flat frame carries about
                // nine more passes than its predecessor at the same draw total, so which
                // entries are empty is half of what this sequence has to say.
                sb.Append($"[{p}]d{_slotWatched[slot][p].Draws}:");
                sb.Append(uniform ? $"UNIFORM(0x{px[0]:X8})" : "varied");
            }

            return sb.Length == 0 ? "none" : sb.ToString();
        }

        public static void EndPass(ulong drawsInPass, PassEndReason reason)
        {
            if (_openIndex >= 0)
            {
                _pending[_openIndex].Draws += drawsInPass;
                _openIndex = -1;
            }

            if (_openWatched)
            {
                _pendingWatched[_pendingWatchedCount].Draws = drawsInPass;
                _pendingWatched[_pendingWatchedCount].Reason = reason;
                _pendingWatchedCount++;
                _openWatched = false;
                _openWatchedTarget = null;
            }
        }

        /// <summary>
        /// Moves the frame just encoded into its slot and starts a fresh accumulator.
        /// </summary>
        public static void Commit(int slot)
        {
            Entry[] target = _slots[slot];

            for (int i = 0; i < _pendingCount; i++)
            {
                target[i] = _pending[i];
            }

            _slotCount[slot] = _pendingCount;

            PassDetail[] watched = _slotWatched[slot];

            for (int i = 0; i < _pendingWatchedCount; i++)
            {
                watched[i] = _pendingWatched[i];
            }

            _slotWatchedCount[slot] = _pendingWatchedCount;

            IntPtr[] sampled = _slotSampled[slot];

            for (int i = 0; i < _pendingSampledCount; i++)
            {
                sampled[i] = _pendingSampled[i];
            }

            _slotSampledCount[slot] = _pendingSampledCount;

            _slotW[slot][0] = _pendingW0; _slotW[slot][1] = _pendingW1;
            _slotW[slot][2] = _pendingW2; _slotW[slot][3] = _pendingW3;
            _slotWSeen[slot] = _pendingWSeen;
            _pendingWSeen = false;

            _slotArgId[slot] = _pendingArgId;
            _slotTexResId[slot] = _pendingTexId;

            _slotOrder[slot] = _wSeen
                ? (_toneSeen
                    ? $"sceneWrites[{_wFirst}..{_wLast}] tonemapAt={_toneAt} {(_toneAt < _wFirst ? "TONEMAP-FIRST" : "writes-first")}"
                    : $"sceneWrites[{_wFirst}..{_wLast}] tonemap=absent")
                : (_toneSeen ? $"noSceneWrites tonemapAt={_toneAt}" : "neither");

            _wSeen = false;
            _toneSeen = false;
            _writeIndexThisFrame = 0;

            lock (_pendingWrites)
            {
                _slotWrites[slot] = _pendingWrites.Count == 0 ? "none" : string.Join(",", _pendingWrites);
                _pendingWrites.Clear();
            }

            _slotMaxAbs[slot] = _pendingMaxAbs;
            _slotNonFinite[slot] = _pendingNonFinite;
            _slotScanned[slot] = _pendingScanned;

            _pendingCount = 0;
            _pendingWatchedCount = 0;
            _pendingSampledCount = 0;
            _pendingMaxAbs = 0f;
            _pendingNonFinite = 0;
            _pendingScanned = 0;
            _openIndex = -1;
            _openWatched = false;
        }

        // The last unexamined input: the constants those draws read. The presented white
        // decodes to ~0.99, not an overflowed magnitude, so the stage is tonemapped
        // output rather than raw radiance - something is mapping the whole scene to
        // white, which is what a blown-up exposure or a non-finite factor does. This
        // scans every constant the watched draws bind, for exactly that.
        private static float _pendingMaxAbs;
        private static int _pendingNonFinite;
        private static int _pendingScanned;

        private static readonly float[] _slotMaxAbs = new float[Slots];
        private static readonly int[] _slotNonFinite = new int[Slots];
        private static readonly int[] _slotScanned = new int[Slots];

        // The tonemap's luminance weights. If these reach the shader as zero the
        // luminance collapses to a fixed multiple of one channel, the divide by it
        // yields a constant well above 1, and every channel clamps to white - which is
        // why a white frame is uniform regardless of what the scene contains.
        private static float _pendingW0, _pendingW1, _pendingW2, _pendingW3;
        private static bool _pendingWSeen;

        private static readonly float[][] _slotW = CreateW();
        private static readonly bool[] _slotWSeen = new bool[Slots];

        private static float[][] CreateW()
        {
            float[][] a = new float[Slots][];
            for (int i = 0; i < Slots; i++)
            {
                a[i] = new float[4];
            }
            return a;
        }

        // The two images the tonemap reads: scene at texture index 8, bloom at index 10.
        // Its curve asymptotes to 1.0, so it turns white legitimately when handed an
        // out-of-range image - this samples both inputs so the blown one can be named.
        private static Texture _pendingSceneTex, _pendingBloomTex;

        private static MTLBuffer _inputBuf;
        // 25, not 9. Xcode shows one of these inputs as almost entirely black while a
        // nine-point sample of it read ~1.0 - every point had landed on sky. Too few
        // samples is the same mistake the flat-frame criterion made.
        private const int InputGridSide = 5;
        private const int InputPixels = InputGridSide * InputGridSide;
        private const int InputBytes = InputPixels * 4;
        private const int InputsPerFrame = 4;

        private const int MaxToneInputs = 8;
        private static readonly int[] _toneIdx = new int[MaxToneInputs];
        private static readonly Texture[] _toneTex = new Texture[MaxToneInputs];
        private static int _toneCount;

        /// <summary>
        /// Every texture the tonemap program binds, latched on first sight. The slot
        /// numbers were guessed before and the guess was wrong, so nothing is assumed
        /// here - each one is reported with its size and format so the scene input can
        /// be picked out by inspection.
        /// </summary>
        // What actually lands in the argument buffer for the tonemap's scene slot, next
        // to the resource id of the texture we believe is bound. Every structural theory
        // is exhausted and the shader still reads 1.0, so the remaining question is
        // whether the id handed to the GPU is the one the binding intended.
        private static ulong _pendingArgId, _pendingTexId;
        private static readonly ulong[] _slotArgId = new ulong[Slots];
        private static readonly ulong[] _slotTexResId = new ulong[Slots];

        public static void NoteToneMapSlotId(int index, ulong argId, TextureBase storage)
        {
            if (!Enabled || index != 128 || storage is not Texture t)
            {
                return;
            }

            _pendingArgId = argId;
            _pendingTexId = t.GetHandle().GpuResourceID._impl;
        }

        public static string DescribeSlotId(int slot)
        {
            ulong a = _slotArgId[slot], t = _slotTexResId[slot];
            Texture tex = _toneCount > 0 ? _toneTex[0] : null;
            string mips = tex == null
                ? "?"
                : $"levels={tex.Info.Levels} firstLevel={tex.FirstLevel} depth={tex.Info.Depth} firstLayer={tex.FirstLayer}";
            return $"argBufId=0x{a:X} texResId=0x{t:X} {(a == t ? "match" : "MISMATCH")} {mips}";
        }

        /// <summary>
        /// Registers a texture sampled while the watched full-resolution target is bound.
        /// Identified by shape, not by shader label: labels are not stable across runs,
        /// and a label-keyed target set silently stayed empty in an earlier bisect, which
        /// made "skipping the writes changes nothing" mean "nothing was ever skipped".
        /// </summary>
        // Set while binding resources for a draw that samples the scene buffer into the
        // full-resolution target - the tonemap draw, identified by shape so it survives
        // DebugLabel changing between runs.
        private static bool _toneDrawPending;

        public static bool TakeToneDrawFlag()
        {
            bool v = _toneDrawPending;
            _toneDrawPending = false;
            return v;
        }

        /// <summary>
        /// Whether the tonemap's own draw should be dropped this frame. Paired: applied
        /// on even present indices only, with odd frames as the control.
        /// </summary>
        public static bool ShouldSkipToneDraw()
        {
            return Enabled && _skipToneDraw && (PresentProbe.Frame & 1) == 0;
        }

        private static bool _skipToneDraw;
        private static bool _skipPresentWrites;

        public static void SetSkipPresentWrites(bool value)
        {
            _skipPresentWrites = value;
        }

        /// <summary>
        /// Draws into the present-sized LDR buffer - the stage between the tonemap's
        /// float target and the texture handed to Present, which is the last one that has
        /// never been isolated. Paired on even present indices.
        /// </summary>
        public static bool ShouldSkipPresentWrite(Texture rt0)
        {
            if (!Enabled || !_skipPresentWrites || (PresentProbe.Frame & 1) != 0)
            {
                return false;
            }

            return rt0 != null &&
                rt0.Width >= 1900 &&
                (rt0.MtlFormat == MTLPixelFormat.RGBA8Unorm ||
                 rt0.MtlFormat == MTLPixelFormat.RGBA8UnormsRGB ||
                 rt0.MtlFormat == MTLPixelFormat.BGRA8Unorm);
        }

        public static void SetSkipToneDraw(bool value)
        {
            _skipToneDraw = value;
        }

        public static void NoteSampledUnderWatchedTarget(int index, TextureBase storage, Texture renderTarget)
        {
            if (!IsWatchedTarget(renderTarget) || storage is not Texture t)
            {
                return;
            }

            if (t.Width < 1280 || t.MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return;
            }

            _toneDrawPending = true;

            NoteToneMapInput(index, storage);
        }

        public static void NoteToneMapInput(int index, TextureBase storage)
        {
            if (storage is not Texture t || _toneCount >= MaxToneInputs)
            {
                return;
            }

            for (int i = 0; i < _toneCount; i++)
            {
                if (_toneIdx[i] == index)
                {
                    _toneTex[i] = t;
                    return;
                }
            }

            _toneIdx[_toneCount] = index;
            _toneTex[_toneCount] = t;
            _toneCount++;
        }

        /// <summary>
        /// Blits both tonemap inputs into the readback buffer. Called from present, where
        /// no pass is open and a blit encoder is legal.
        /// </summary>
        public static void SampleInputs(CommandBufferScoped cbs, int slot)
        {
            if (_inputBuf.NativePtr == IntPtr.Zero || _toneCount == 0)
            {
                return;
            }

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            for (int k = 0; k < _toneCount && k < InputsPerFrame; k++)
            {
                Texture t = _toneTex[k];
                MTLTexture tex = t.GetHandle();

                if (tex.NativePtr == IntPtr.Zero)
                {
                    continue;
                }

                ulong baseOffset = (ulong)((slot * InputsPerFrame + k) * InputBytes);

                for (int i = 0; i < InputPixels; i++)
                {
                    blit.CopyFromTexture(
                        tex, 0, 0,
                        new MTLOrigin { x = (ulong)(t.Width * (i % 3 + 1) / 4), y = (ulong)(t.Height * (i / 3 + 1) / 4), z = 0 },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _inputBuf, baseOffset + (ulong)(i * 4), 4, 4);
                }
            }
        }

        // Non-render writes to the tonemap's scene input. No render pass ever names it
        // as an attachment, yet its contents change every frame - so whatever fills it
        // is a copy or an upload, and its ordering against the tonemap's read is what
        // decides whether the frame comes out white.
        private static readonly System.Collections.Generic.List<string> _pendingWrites = [];
        private static readonly string[] _slotWrites = new string[Slots];

        public static void NoteNonRenderWrite(Texture target, string kind)
        {
            if (!Enabled || target == null || _toneCount == 0)
            {
                return;
            }

            for (int i = 0; i < _toneCount; i++)
            {
                if (RootOf(_toneTex[i]) != IntPtr.Zero && RootOf(_toneTex[i]) == RootOf(target))
                {
                    lock (_pendingWrites)
                    {
                        if (_pendingWrites.Count < 32)
                        {
                            _pendingWrites.Add($"{kind}@slot{_toneIdx[i]}");
                        }
                    }

                    return;
                }
            }
        }

        // Which programs draw into the tonemap's scene input. Ordering is excluded and
        // the shader reads 1.0, so the writes themselves must be producing it - these are
        // the shaders to examine next.
        private static readonly System.Collections.Generic.HashSet<string> _writers = [];

        public static void NoteWriterProgram(Texture target, string label)
        {
            if (!Enabled || label == null || _toneCount == 0)
            {
                return;
            }

            IntPtr root = RootOf(target);

            for (int i = 0; i < _toneCount; i++)
            {
                if (root != IntPtr.Zero && root == RootOf(_toneTex[i]))
                {
                    lock (_writers)
                    {
                        _writers.Add(label);
                    }

                    return;
                }
            }
        }

        // Encode order inside one frame: where the writes to the tonemap's input land in
        // the draw sequence, and where the tonemap's own draw lands. If the tonemap is
        // encoded before the writes, no GPU-side ordering fix can help - the command
        // stream itself is built in the wrong order.
        private static ulong _wFirst, _wLast, _toneAt;
        private static bool _wSeen, _toneSeen;

        private static readonly string[] _slotOrder = new string[Slots];

        // Runtime bisect. Shader DebugLabels are not stable across runs, so a label list
        // captured in one run cannot be painted in the next - and the compile-time patch
        // is also defeated by a warm shader cache. Selecting by position among the draws
        // that write the tonemap's input avoids both: it needs no labels, no recompile,
        // and can be switched while the game runs.
        private static int _skipLo = -1, _skipHi = -1;
        private static int _writeIndexThisFrame;

        public static void SetSkipRange(int lo, int hi)
        {
            _skipLo = lo;
            _skipHi = hi;
        }

        /// <summary>
        /// Whether this draw writes the tonemap's input and falls in the skipped range.
        /// </summary>
        public static bool ShouldSkipDraw(Texture rt0)
        {
            if (!Enabled || _toneCount == 0 || _skipLo < 0)
            {
                return false;
            }

            IntPtr root = RootOf(rt0);

            if (root == IntPtr.Zero)
            {
                return false;
            }

            for (int i = 0; i < _toneCount; i++)
            {
                if (root == RootOf(_toneTex[i]))
                {
                    // Even frames only: the odd frames are the paired control.
                    if ((PresentProbe.Frame & 1) != 0)
                    {
                        return false;
                    }

                    int index = _writeIndexThisFrame;
                    return index >= _skipLo && index < _skipHi;
                }
            }

            return false;
        }

        public static void NoteDraw(Texture rt0, string label, ulong drawIndex)
        {
            if (!Enabled || _toneCount == 0)
            {
                return;
            }

            if (label == "3ebc3a8f6b77cc8f")
            {
                if (!_toneSeen)
                {
                    _toneAt = drawIndex;
                    _toneSeen = true;
                }

                return;
            }

            IntPtr root = RootOf(rt0);

            if (root == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < _toneCount; i++)
            {
                if (root == RootOf(_toneTex[i]))
                {
                    if (!_wSeen)
                    {
                        _wFirst = drawIndex;
                        _wSeen = true;
                    }

                    _wLast = drawIndex;
                    _writeIndexThisFrame++;
                    return;
                }
            }
        }

        public static string DescribeOrder(int slot)
        {
            return _slotOrder[slot] ?? "n/a";
        }

        public static string DescribeWriterPrograms()
        {
            lock (_writers)
            {
                return _writers.Count == 0 ? "none" : string.Join(",", _writers);
            }
        }

        // Was the texture actually handed to Present written by anything this frame?
        // If white frames are exactly the frames where it was not, the symptom is a
        // missing composite rather than a wrong colour.
        private static readonly bool[] _slotPresentWritten = new bool[Slots];
        private static readonly int[] _slotPresentPasses = new int[Slots];
        private static readonly ulong[] _slotPresentDraws = new ulong[Slots];

        // The present source is never a render pass attachment, on white and good frames
        // alike, so whatever fills it is a copy. These are the roots seen at Present -
        // they alternate between a couple of textures - so a copy landing on one of them
        // can be counted during the frame.
        private static readonly IntPtr[] _presentRoots = new IntPtr[4];
        private static int _presentRootCount;
        private static int _pendingPresentCopies;
        private static readonly int[] _slotPresentCopies = new int[Slots];

        private static void RememberPresentRoot(IntPtr root)
        {
            if (root == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < _presentRootCount; i++)
            {
                if (_presentRoots[i] == root)
                {
                    return;
                }
            }

            if (_presentRootCount < _presentRoots.Length)
            {
                _presentRoots[_presentRootCount++] = root;
            }
        }

        public static void NoteCopyInto(Texture destination)
        {
            if (!Enabled)
            {
                return;
            }

            IntPtr root = RootOf(destination);

            for (int i = 0; i < _presentRootCount; i++)
            {
                if (_presentRoots[i] == root)
                {
                    _pendingPresentCopies++;
                    return;
                }
            }
        }

        public static void NotePresentSource(Texture src, int slot)
        {
            RememberPresentRoot(RootOf(src));
            _slotPresentCopies[slot] = _pendingPresentCopies;
            _pendingPresentCopies = 0;

            IntPtr root = RootOf(src);
            bool found = false;
            int passes = 0;
            ulong draws = 0;

            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Target == root)
                {
                    found = true;
                    passes = _pending[i].Passes;
                    draws = _pending[i].Draws;
                    break;
                }
            }

            _slotPresentWritten[slot] = found;
            _slotPresentPasses[slot] = passes;
            _slotPresentDraws[slot] = draws;
        }

        // Self-check: a good frame's present source must have a writer. If this fires,
        // attribution is broken again and no ownership result can be trusted.
        private static int _attributionFailures;

        public static void CheckAttribution(int slot, bool frameWasWhite)
        {
            if (frameWasWhite || _slotPresentWritten[slot] || _slotPresentCopies[slot] > 0)
            {
                return;
            }

            if (++_attributionFailures % 120 == 1)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"hdrprobe ATTRIBUTION-BROKEN: a good frame's present source has no writer " +
                    $"({_attributionFailures} so far). Ownership results are not trustworthy.");
            }
        }

        public static string DescribePresentWritten(int slot)
        {
            return _slotPresentWritten[slot]
                ? $"passes={_slotPresentPasses[slot]} draws={_slotPresentDraws[slot]} copies={_slotPresentCopies[slot]}"
                : $"no-pass copies={_slotPresentCopies[slot]}";
        }

        public static string DescribeWrites(int slot)
        {
            return _slotWrites[slot] ?? "none";
        }

        public static unsafe string DescribeInputs(int slot)
        {
            if (_inputBuf.NativePtr == IntPtr.Zero)
            {
                return "n/a";
            }

            uint* words = (uint*)_inputBuf.Contents;
            StringBuilder sb = new();

            for (int k = 0; k < InputsPerFrame; k++)
            {
                uint* px = words + (slot * InputsPerFrame + k) * InputPixels;
                uint min = uint.MaxValue, max = 0;

                for (int i = 0; i < InputPixels; i++)
                {
                    if (px[i] < min) min = px[i];
                    if (px[i] > max) max = px[i];
                }

                Texture t = k < _toneCount ? _toneTex[k] : null;

                // The raw handle as well as the canonical one. A white frame carries two
                // 1600x896 RG11B10Float textures - one taking 29 draws across 25 passes,
                // and one with a single pass and no draws at all - and if they alias the
                // same guest memory their canonical pointers are equal, so a report keyed
                // on canonical identity cannot tell which of the two was sampled. That is
                // the merge that has produced four wrong conclusions in these notes, and
                // here it would hide exactly the case worth checking: the composite reading
                // the one nothing ever drew into.
                sb.Append(t == null
                    ? $" [{k}:none]"
                    : $" [slot{_toneIdx[k]}:{t.Width}x{t.Height}:{t.MtlFormat}:" +
                      $"handle0x{t.GetHandle().NativePtr:X}:root0x{RootOf(t):X}]");
                sb.Append($"0x{min:X8}..0x{max:X8}");
            }

            return sb.ToString();
        }

        public static unsafe void NoteToneMapWeights(IntPtr contents, int offset)
        {
            if (contents == IntPtr.Zero)
            {
                return;
            }

            float* d = (float*)((byte*)contents + offset);
            _pendingW0 = d[0]; _pendingW1 = d[1]; _pendingW2 = d[2]; _pendingW3 = d[3];
            _pendingWSeen = true;
        }

        public static string DescribeWeights(int slot)
        {
            if (!_slotWSeen[slot])
            {
                return "not-bound";
            }

            float[] w = _slotW[slot];
            return $"cb1[0]=({w[0]:G6},{w[1]:G6},{w[2]:G6},{w[3]:G6})";
        }

        public static unsafe void NoteUniform(IntPtr contents, int offset, int size)
        {
            if (contents == IntPtr.Zero || size <= 0)
            {
                return;
            }

            int floats = Math.Min(size, 1024) / sizeof(float);
            float* data = (float*)((byte*)contents + offset);

            for (int i = 0; i < floats; i++)
            {
                float v = data[i];

                if (float.IsNaN(v) || float.IsInfinity(v))
                {
                    _pendingNonFinite++;
                    continue;
                }

                float a = MathF.Abs(v);

                if (a > _pendingMaxAbs)
                {
                    _pendingMaxAbs = a;
                }
            }

            _pendingScanned++;
        }

        public static string DescribeUniforms(int slot)
        {
            return $"bufs={_slotScanned[slot]},maxAbs={_slotMaxAbs[slot]:G6},nonFinite={_slotNonFinite[slot]}";
        }

        /// <summary>
        /// The distinct textures the watched stage sampled, in first-seen order.
        /// </summary>
        public static string DescribeSampled(int slot)
        {
            int count = _slotSampledCount[slot];

            if (count == 0)
            {
                return "none";
            }

            IntPtr[] sampled = _slotSampled[slot];
            StringBuilder sb = new();

            for (int i = 0; i < count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append($"0x{sampled[i]:X}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// The full resolution HDR target's passes in encode order, as draws(endReason).
        /// </summary>
        public static string DescribeWatched(int slot)
        {
            int count = _slotWatchedCount[slot];

            if (count == 0)
            {
                return "none";
            }

            PassDetail[] details = _slotWatched[slot];
            StringBuilder sb = new();

            for (int i = 0; i < count; i++)
            {
                ref PassDetail d = ref details[i];

                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }

                sb.Append($"{d.Draws}d({d.Reason}{(d.Cleared ? ",CLEAR" : "")})");
            }

            return sb.ToString();
        }

        public static string Describe(int slot)
        {
            int count = _slotCount[slot];

            if (count == 0)
            {
                return "none";
            }

            Entry[] entries = _slots[slot];
            StringBuilder sb = new();

            for (int i = 0; i < count; i++)
            {
                ref Entry e = ref entries[i];

                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append($"[0x{e.Target:X}:{e.Width}x{e.Height}:{e.Format}:p={e.Passes},d={e.Draws},clr={e.Cleared}]");
            }

            return sb.ToString();
        }
    }
}
