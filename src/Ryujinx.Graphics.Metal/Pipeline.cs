using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.QuartzCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Why a render pass was ended. Passes are the expensive unit on a tile based
    /// GPU, so the stats line attributes them to whatever forced the split.
    /// </summary>
    public enum PassEndReason
    {
        Unspecified,
        SwapState,
        FragmentDependencySkipped,
        Present,
        Flush,
        DepthDump,
        FragmentDependency,
        ColorMask,
        RenderTargets,
        Counter,
        BlitEncoder,
        ComputeEncoder,
        Dispose,
    }

    public enum EncoderType
    {
        Blit,
        Compute,
        Render,
        None
    }

    [SupportedOSPlatform("macos")]
    class Pipeline : IPipeline, IEncoderFactory, IDisposable
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024; // MiB
        private const int MaxDisposedResourceCountForFlush = 4096;
        private const int SyncStatsLogFrameInterval = 120;

        private readonly MTLDevice _device;
        private readonly MetalRenderer _renderer;
        private EncoderStateManager _encoderStateManager;
        private ulong _byteWeight;
        private int _disposedResourceCount;
        private int _presentCount;

        // The per-frame slot the probes index by. Passes encoded now belong to the frame
        // this present will close, which is the same numbering PresentProbe classifies by.
        internal int FrameSlot => _presentCount;
        private int _autoFlushDrawCount;
        private int _autoFlushAttachmentCount;

        // Render passes are the expensive unit on a tile based GPU: each one loads and
        // stores every attachment. Counted here against draws so the stats line says
        // how many draws a pass is actually amortised over.
        private ulong _renderPassCount;
        private ulong _lastStatsDrawCount;
        private ulong _lastStatsRenderPassCount;
        private readonly int[] _passEndReasons = new int[Enum.GetValues<PassEndReason>().Length];
        private ulong _drawCountAtPassStart;

        public ulong DrawCount { get; private set; }
        public ulong DispatchCount { get; private set; }

        public MTLCommandBuffer CommandBuffer;

        public IndexBufferPattern QuadsToTrisPattern;
        public IndexBufferPattern TriFanToTrisPattern;

        internal CommandBufferScoped? PreloadCbs { get; private set; }
        internal CommandBufferScoped Cbs { get; private set; }
        internal CommandBufferEncoder Encoders => Cbs.Encoders;
        internal EncoderType CurrentEncoderType => Encoders.CurrentEncoderType;
        internal bool SupportsSamplesPassed => _renderer.Counters.SupportsSamplesPassed;

        public Pipeline(MTLDevice device, MetalRenderer renderer)
        {
            _device = device;
            _renderer = renderer;

            renderer.CommandBufferPool.Initialize(this);

            CommandBuffer = (Cbs = _renderer.CommandBufferPool.Rent()).CommandBuffer;
        }

        internal void InitEncoderStateManager(BufferManager bufferManager)
        {
            _encoderStateManager = new EncoderStateManager(_device, bufferManager, this);

            QuadsToTrisPattern = new IndexBufferPattern(_renderer, 4, 6, 0, [0, 1, 2, 0, 2, 3], 4, false);
            TriFanToTrisPattern = new IndexBufferPattern(_renderer, 3, 3, 2, [int.MinValue, -1, 0], 1, true);
        }

        public EncoderState SwapState(EncoderState state, DirtyFlags flags = DirtyFlags.All, bool endRenderPass = true)
        {
            if (endRenderPass && CurrentEncoderType == EncoderType.Render)
            {
                EndCurrentPass(PassEndReason.SwapState);
            }

            return _encoderStateManager.SwapState(state, flags);
        }

        public PredrawState SavePredrawState()
        {
            return _encoderStateManager.SavePredrawState();
        }

        public void RestorePredrawState(PredrawState state)
        {
            _encoderStateManager.RestorePredrawState(state);
        }

        public void SetClearLoadAction(bool clear)
        {
            _encoderStateManager.SetClearLoadAction(clear);
        }

        public MTLRenderCommandEncoder GetOrCreateRenderEncoder(bool forDraw = false)
        {
            // Mark all state as dirty to ensure it is set on the new encoder
            if (Cbs.Encoders.CurrentEncoderType != EncoderType.Render)
            {
                _encoderStateManager.SignalRenderDirty();
            }

            // A draw that samples one of its own attachments reads undefined data on
            // Metal. End the pass first so it reads finished writes instead. Decided from
            // bound state before the prepass runs and before any encoder is acquired: the
            // previous version decided during the prepass and had to end the pass from
            // inside encoder acquisition, which faulted the driver at drawIndexedPrimitives.
            // Only when the pass already carries draws - with none there is nothing to
            // order, and that also stops this looping, since the feedback does not go away.
            if (forDraw && (FeedbackProbe.Fix || FeedbackProbe.FixLive) &&
                Cbs.Encoders.CurrentEncoderType == EncoderType.Render &&
                DrawCount != _drawCountAtPassStart &&
                _encoderStateManager.SamplesOwnAttachment())
            {
                EndCurrentPass(PassEndReason.FragmentDependency);
                _encoderStateManager.SignalRenderDirty();
            }

            if (forDraw)
            {
                _encoderStateManager.RenderResourcesPrepass();
            }

            // Before the pass opens, while switching encoders is still legal, record what
            // the watched target holds. The blit encoder this may open is closed again by
            // EnsureRenderEncoder below.
            if (HdrPassProbe.Enabled && Cbs.Encoders.CurrentEncoderType != EncoderType.Render)
            {
                HdrPassProbe.SampleBeforePass(Cbs, _encoderStateManager.RenderTargets[0], _presentCount % 4);
            }

            MTLRenderCommandEncoder renderCommandEncoder = Cbs.Encoders.EnsureRenderEncoder();

            if (forDraw)
            {
                _encoderStateManager.RebindRenderState(renderCommandEncoder);
            }

            return renderCommandEncoder;
        }

        public MTLBlitCommandEncoder GetOrCreateBlitEncoder()
        {
            return Cbs.Encoders.EnsureBlitEncoder();
        }

        public MTLComputeCommandEncoder GetOrCreateComputeEncoder(bool forDispatch = false)
        {

            // Mark all state as dirty to ensure it is set on the new encoder
            if (Cbs.Encoders.CurrentEncoderType != EncoderType.Compute)
            {
                _encoderStateManager.SignalComputeDirty();
            }

            if (forDispatch)
            {
                _encoderStateManager.ComputeResourcesPrepass();
            }

            MTLComputeCommandEncoder computeCommandEncoder = Cbs.Encoders.EnsureComputeEncoder();

            if (forDispatch)
            {
                _encoderStateManager.RebindComputeState(computeCommandEncoder);
            }

            return computeCommandEncoder;
        }

        private PassEndReason _pendingPassEndReason = PassEndReason.Unspecified;

        private readonly List<BufferHolder> _activeBufferMirrors = [];

        /// <summary>
        /// Remembers a buffer that has live mirrors, so they can all be dropped when the
        /// command buffer changes and their staging reservations go with it.
        /// </summary>
        public void RegisterActiveMirror(BufferHolder buffer)
        {
            _activeBufferMirrors.Add(buffer);
        }

        public void ClearActiveMirrors()
        {
            foreach (BufferHolder buffer in _activeBufferMirrors)
            {
                buffer.ClearMirrors();
            }

            _activeBufferMirrors.Clear();
        }

        /// <summary>
        /// Marks bound buffer sets dirty so the next draw resolves the range again and
        /// picks up, or stops using, a mirror. Metal rebuilds the argument buffers for a
        /// dirty set wholesale, so this does not need to name the individual binding.
        /// </summary>
        public void RebindBufferRange(Auto<DisposableBuffer> buffer, int offset, int size)
        {
            _encoderStateManager.SignalBufferRebind();
        }

        // FrameProbe needs the live command buffer to blit a patch out mid-frame. Both
        // are thin wrappers so the probe never reaches into pipeline internals itself.
        internal CommandBufferScoped CurrentCbs => Cbs;

        internal void EndCurrentPassForProbe()
        {
            EndCurrentPass(PassEndReason.Unspecified);
        }

        public void EndCurrentPass(PassEndReason reason = PassEndReason.Unspecified)
        {
            _pendingPassEndReason = reason;

            Cbs.Encoders.EndCurrentPass();

            _pendingPassEndReason = PassEndReason.Unspecified;

            // Sample the watched target right after a pass on it ends. Sampling only on
            // encoder transitions left every chart entry between transitions holding the
            // previous frame's pixels - the fault Phase 1's first run exposed.
            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.SampleBoundary(Cbs, _presentCount % 4);
            }
        }

        public void OnRenderPassEnded(EncoderType startingType)
        {
            PassEndReason reason = startingType switch
            {
                EncoderType.Blit => PassEndReason.BlitEncoder,
                EncoderType.Compute => PassEndReason.ComputeEncoder,
                _ => _pendingPassEndReason,
            };

            _passEndReasons[(int)reason]++;

            if (PresentProbe.Enabled)
            {
                ulong drawsInPass = DrawCount - _drawCountAtPassStart;
                PresentProbe.RecordPass(_presentCount, reason, drawsInPass);
                HdrPassProbe.EndPass(drawsInPass, reason);
            }
        }

        // Diagnostic: how often a new pass uses an attachment set seen among the
        // last few passes. same = identical to the previous pass (mergeable with no
        // reordering at all); aba = identical to two passes back (mergeable only by
        // reordering across one intervening pass). Bounds what pass merging is worth
        // before anything is built.
        private readonly ulong[] _passSignatureRing = new ulong[4];
        private int _passSignatureCount;
        private int _passRevisitSame;
        private int _passRevisitAba;

        private ulong ComputePassSignature()
        {
            ulong hash = 17;

            Texture[] targets = _encoderStateManager.RenderTargets;

            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] != null)
                {
                    hash = hash * 31 + (ulong)targets[i].GetHandle().NativePtr + (ulong)i;
                }
            }

            if (_encoderStateManager.DepthStencil != null)
            {
                hash = hash * 31 + (ulong)_encoderStateManager.DepthStencil.GetHandle().NativePtr;
            }

            return hash;
        }

        public MTLRenderCommandEncoder CreateRenderCommandEncoder()
        {
            _renderPassCount++;
            _drawCountAtPassStart = DrawCount;

            ulong signature = ComputePassSignature();

            if (_passSignatureCount > 0 && signature == _passSignatureRing[(_passSignatureCount - 1) & 3])
            {
                _passRevisitSame++;
            }
            else if (_passSignatureCount > 1 && signature == _passSignatureRing[(_passSignatureCount - 2) & 3])
            {
                _passRevisitAba++;
            }

            _passSignatureRing[_passSignatureCount & 3] = signature;
            _passSignatureCount++;

            return _encoderStateManager.CreateRenderCommandEncoder();
        }

        public ulong PrepareCounterRenderPass(MTLRenderPassDescriptor descriptor)
        {
            return _renderer.Counters.PrepareRenderPass(descriptor, Cbs);
        }

        public MTLComputeCommandEncoder CreateComputeCommandEncoder()
        {
            return _encoderStateManager.CreateComputeCommandEncoder();
        }

        public void FixupStoreActions(MTLRenderCommandEncoder encoder)
        {
            _encoderStateManager.FixupStoreActions(encoder, DrawCount - _drawCountAtPassStart);
        }

        // Diagnostic: RYUJINX_METAL_LOG_PRESENT=1 logs the source texture of every
        // presented frame with a wall clock stamp, so single-frame artefacts caught on
        // a screen recording can be correlated with dynamic resolution switches.
        private static readonly bool _logPresent =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_LOG_PRESENT") == "1";

        private (IntPtr Handle, int W, int H) _lastPresented;

        [ThreadStatic]
        private static IntPtr _frameAutoreleasePool;

        public void Present(CAMetalDrawable drawable, Texture src, Extents2D srcRegion, Extents2D dstRegion, bool isLinear, bool useFsrSharpener, float scalingFilterLevel)
        {
            // Drain everything autoreleased on the render thread since the last
            // present (encoders, command buffers, drawables and their driver-side
            // shadows), then open the next frame's pool. Without this the thread
            // has no pool at all and every autoreleased +1 is permanent.
            if (_frameAutoreleasePool != IntPtr.Zero)
            {
                ObjcOwnership.objc_autoreleasePoolPop(_frameAutoreleasePool);
            }

            _frameAutoreleasePool = ObjcOwnership.objc_autoreleasePoolPush();

            if (_logPresent)
            {
                // Log only when the presented texture changes identity or size; a line
                // per frame perturbs timing enough to mask the very race under study.
                (IntPtr Handle, int W, int H) now = (src.GetHandle().NativePtr, src.Width, src.Height);

                if (now != _lastPresented)
                {
                    _lastPresented = now;

                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        $"present-change {now.W}x{now.H} handle=0x{now.Handle:X} wall={DateTime.Now:HH:mm:ss.fff}");
                }
            }

            _lastPresentSource = src;
            _drawCountAtFrameStart = DrawCount;

            if (_stainSweep)
            {
                // 24 steps over a ~3000 draw frame, cycling so the whole range is covered
                // several times inside one trigger window.
                _stainAtDraw = (_presentCount % 24) * 128;
                PresentProbe.NoteStainIndex(_stainAtDraw);
            }

            _renderer.FrameCapture.CurrentCommandBuffer = CommandBuffer;
            _renderer.FrameCapture.OnPresentBegin();

            AppliedRenderState.RefreshToggle();
            EncoderStateManager.RefreshDedupToggle();
            FeedbackProbe.RefreshToggle();
            RefreshSkipDispatch();
            OpRing.OnPresent();
            FlashGuard.RefreshToggle();
            RefreshBarrierToggle();
            EncoderStateManager.RefreshSamplingToggle();

            if (DrawRing.Enabled)
            {
                DrawRing.OnPresent();
            }

            if (ToneMapProbe.Enabled)
            {
                ToneMapProbe.OnPresent();
            }

            if (PresentProbe.Enabled)
            {
                PresentProbe.OnPresent(Cbs, src, DrawCount, DispatchCount);
            }

            // After the classification above has read this frame's slot, and before the
            // next frame encodes anything into its own.
            CoverageProbe.OnPresent(Cbs, _presentCount + 1);

            // TODO: Clean this up
            TextureCreateInfo textureInfo = new((int)drawable.Texture.Width, (int)drawable.Texture.Height, (int)drawable.Texture.Depth, (int)drawable.Texture.MipmapLevelCount, (int)drawable.Texture.SampleCount, 0, 0, 0, Format.B8G8R8A8Unorm, 0, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
            Texture dst = new(_device, _renderer, this, textureInfo, drawable.Texture, 0, 0);

            // Positive control for the stain probe: staining here must turn the presented
            // frame green. If it does not, the stain never took effect and a "no green
            // ever came back" result from the post-present stain means nothing.
            if (_stainBeforePresent)
            {
                // A pass may still be open here; creating an encoder on top of one is an
                // immediate driver assertion. The post-present stain happens after
                // EndCurrentPass and so never hit this.
                EndCurrentPass(PassEndReason.Unspecified);

                MTLRenderPassDescriptor pre = new();
                MTLRenderPassColorAttachmentDescriptor pa = pre.ColorAttachments.Object(0);
                pa.Texture = src.GetIdentityHandle(Cbs);
                pa.LoadAction = MTLLoadAction.Clear;
                pa.StoreAction = MTLStoreAction.Store;
                pa.ClearColor = new MTLClearColor { red = 0.0, green = 1.0, blue = 0.0, alpha = 1.0 };

                MTLRenderCommandEncoder preEncoder = CommandBuffer.RenderCommandEncoder(pre);
                preEncoder.EndEncoding();
                pre.Dispose();
            }

            if (FlashGuard.Enabled)
            {
                // Fold this frame into the kept image unless it is flat, then present the
                // kept image. Both steps decide in the shader, for the frame they apply
                // to - the earlier variants either needed a CPU stall to know in time, or
                // fell back on a surface from the rotation that was often flat itself.
                Texture keep = FlashGuard.GetKeepTexture(_device, _renderer, this, src);

                if (keep != null)
                {
                    // Register both against this command buffer before the pass names them.
                    // The parameterless GetHandle does not register, and this texture is
                    // reachable only from a static field, so nothing else stops it being
                    // recycled while the frame that attaches it is still in flight.
                    keep.GetHandle(Cbs);
                    src.GetHandle(Cbs);

                    _renderer.HelperShader.UpdateKeepGood(Cbs, src, keep);
                    _renderer.HelperShader.BlitColor(Cbs, keep, dst, srcRegion, dstRegion, isLinear, true);
                }
                else
                {
                    _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, isLinear, true);
                }
            }
            else if (useFsrSharpener)
            {
                _renderer.HelperShader.PresentColor(Cbs, src, dst, srcRegion, dstRegion, scalingFilterLevel / 100f, true);
            }
            else
            {
                _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, isLinear, true);
            }

            _guardPreviousSource = src;

            EndCurrentPass(PassEndReason.Present);

            // Attribution-free probe: after presenting, stain the source a colour nothing
            // in the scene produces. If it comes back on a later frame still stained, the
            // frame was presented without anything having drawn into it - which separates
            // "something wrote white" from "nothing wrote at all". Bookkeeping-based
            // ownership tracking cannot answer this; its self-check says it is unreliable.
            if (_stainPresentSource)
            {
                MTLRenderPassDescriptor stain = new();
                MTLRenderPassColorAttachmentDescriptor a = stain.ColorAttachments.Object(0);
                a.Texture = src.GetIdentityHandle(Cbs);
                a.LoadAction = MTLLoadAction.Clear;
                a.StoreAction = MTLStoreAction.Store;
                a.ClearColor = new MTLClearColor { red = 0.0, green = 1.0, blue = 0.0, alpha = 1.0 };

                MTLRenderCommandEncoder stainEncoder = CommandBuffer.RenderCommandEncoder(stain);
                stainEncoder.EndEncoding();
                stain.Dispose();
            }

            // Sample the present source and every target this frame passed through.
            // Has to sit here: no pass is open, the present blit is already encoded, and
            // the command buffer has not been committed yet. Read back several frames
            // later, so nothing is ever waited on.
            if (FrameProbe.Enabled)
            {
                FrameProbe.Capture(Cbs, src);
            }

            // The per-present view of the drawable's texture was never released -
            // one native texture view leaked per frame. The Auto defers the native
            // release until the command buffer using it completes, so this is safe
            // before the flush below.
            dst.Release();

            Cbs.CommandBuffer.PresentDrawable(drawable);

            // Balance the ownership taken at nextDrawable time (Window.Present).
            ObjcOwnership.Release(drawable.NativePtr);

            FlushCommandsImpl();

            // A scoped capture has to be told where a frame begins and ends, and nothing
            // was telling it: without this the scope never opened and the trace came out
            // empty, while the queue-scope alternative recorded every command buffer and
            // produced multi-GB bundles the tools aborted while finalising. Bracketing one
            // present-to-present interval gives Xcode exactly one frame.
            _renderer.FrameCapture.EndScope();

            _renderer.AutoFlush.Present();
            _renderer.FrameCapture.ProcessPresent();
            _renderer.FrameCapture.BeginScope();

            _presentCount++;

            if (_presentCount % SyncStatsLogFrameInterval == 0)
            {
                long syncWaitTicks = _renderer.SyncManager.GetAndResetWaitStats(
                    out int syncWaitCount,
                    out int forcedSyncFlushCount,
                    out int proactiveSyncFlushCount,
                    out int coalescedSyncSignalCount,
                    out string waitBreakdown,
                    out string createBreakdown,
                    out string waitDurations,
                    out string threadBreakdown);

                int autoFlushDrawCount = _autoFlushDrawCount;
                int autoFlushAttachmentCount = _autoFlushAttachmentCount;
                _autoFlushDrawCount = 0;
                _autoFlushAttachmentCount = 0;

                if (syncWaitCount != 0 || forcedSyncFlushCount != 0 || proactiveSyncFlushCount != 0 || coalescedSyncSignalCount != 0 ||
                    autoFlushDrawCount != 0 || autoFlushAttachmentCount != 0)
                {
                    double waitMs = syncWaitTicks * 1000.0 / Stopwatch.Frequency;
                    string sourceText = string.IsNullOrEmpty(waitBreakdown) ? string.Empty : $" wait: {waitBreakdown}.";
                    string createText = string.IsNullOrEmpty(createBreakdown) ? string.Empty : $" created: {createBreakdown}.";
                    string durationText = string.IsNullOrEmpty(waitDurations) ? string.Empty : $" durations: {waitDurations}.";
                    string threadText = string.IsNullOrEmpty(threadBreakdown) ? string.Empty : $" threads: {threadBreakdown}.";

                    ulong draws = DrawCount - _lastStatsDrawCount;
                    ulong passes = _renderPassCount - _lastStatsRenderPassCount;
                    _lastStatsDrawCount = DrawCount;
                    _lastStatsRenderPassCount = _renderPassCount;

                    string passText =
                        $" per frame: {passes / (ulong)SyncStatsLogFrameInterval} passes, " +
                        $"{draws / (ulong)SyncStatsLogFrameInterval} draws " +
                        $"({(passes != 0 ? (double)draws / passes : 0):F1} draws/pass).";

                    string reasonText = " pass ends: " + string.Join(", ", Enum.GetValues<PassEndReason>()
                        .Where(r => _passEndReasons[(int)r] != 0)
                        .OrderByDescending(r => _passEndReasons[(int)r])
                        .Select(r => $"{r}={_passEndReasons[(int)r] / SyncStatsLogFrameInterval}"));

                    Array.Clear(_passEndReasons);

                    string revisitText =
                        $" pass revisits: same={_passRevisitSame / SyncStatsLogFrameInterval}, aba={_passRevisitAba / SyncStatsLogFrameInterval} per frame.";
                    _passRevisitSame = 0;
                    _passRevisitAba = 0;

                    string uploadGates = BufferHolder.TakeUploadGates();
                    string gateText = uploadGates == null ? string.Empty : $" upload gates: {uploadGates}.";
                    string blitCallers = CommandBufferEncoder.TakeBlitCallers();
                    string blitText = blitCallers == null ? string.Empty : $" blit callers: {blitCallers}.";

                    Logger.Info?.PrintMsg(
                        LogClass.Gpu,
                        $"Metal sync stats over last {SyncStatsLogFrameInterval} frames: {waitMs:F2}ms in {syncWaitCount} waits, " +
                        $"{forcedSyncFlushCount} forced flushes, {proactiveSyncFlushCount} proactive flushes, " +
                        $"{coalescedSyncSignalCount} coalesced signals, " +
                        $"{autoFlushDrawCount} draw auto-flushes, {autoFlushAttachmentCount} attachment auto-flushes " +
                        $"(fast flush: {_renderer.AutoFlush.FastFlushMode}).{sourceText}{createText}{durationText}{threadText}{passText}{reasonText}{revisitText}{gateText}{blitText}");
                }
            }

            // Cleanup
            dst.Dispose();
        }

        public CommandBufferScoped GetPreloadCommandBuffer()
        {
            PreloadCbs ??= _renderer.CommandBufferPool.Rent();

            return PreloadCbs.Value;
        }

        public void FlushCommandsIfWeightExceeding(IAuto disposedResource, ulong byteWeight)
        {
            bool usedByCurrentCb = disposedResource.HasCommandBufferDependency(Cbs);

            if (PreloadCbs != null && !usedByCurrentCb)
            {
                usedByCurrentCb = disposedResource.HasCommandBufferDependency(PreloadCbs.Value);
            }

            if (usedByCurrentCb)
            {
                // Since we can only free memory after the command buffer that uses a given resource was executed,
                // keeping the command buffer might cause a high amount of memory to be in use.
                // To prevent that, we force submit command buffers if the memory usage by resources
                // in use by the current command buffer is above a given limit, and those resources were disposed.
                _byteWeight += byteWeight;
                _disposedResourceCount++;

                // A large number of tiny Metal resources has significant driver-side
                // overhead even when their combined payload is small. Bound both the
                // byte weight and object count so they cannot accumulate indefinitely.
                if (_byteWeight >= MinByteWeightForFlush || _disposedResourceCount >= MaxDisposedResourceCountForFlush)
                {
                    FlushCommandsImpl();
                }
            }
        }

        public void FlushCommandsImpl()
        {
            _renderer.AutoFlush.RegisterFlush(DrawCount);
            EndCurrentPass(PassEndReason.Flush);

            _byteWeight = 0;
            _disposedResourceCount = 0;

            if (PreloadCbs != null)
            {
                PreloadCbs.Value.Dispose();
                PreloadCbs = null;
            }

            CommandBuffer = (Cbs = _renderer.CommandBufferPool.ReturnAndRent(Cbs)).CommandBuffer;

            // Mirrors live in staging reservations owned by the command buffer that is
            // being retired, so none of them survive the swap.
            ClearActiveMirrors();
            _renderer.RegisterFlush();
        }

        public void DirtyTextures()
        {
            _encoderStateManager.DirtyTextures();
        }

        public void DirtyImages()
        {
            _encoderStateManager.DirtyImages();
        }

        public void Blit(
            Texture src,
            Texture dst,
            Extents2D srcRegion,
            Extents2D dstRegion,
            bool isDepthOrStencil,
            bool linearFilter)
        {
            if (isDepthOrStencil)
            {
                _renderer.HelperShader.BlitDepthStencil(Cbs, src, dst, srcRegion, dstRegion);
            }
            else
            {
                _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, linearFilter);
            }
        }

        public void Barrier()
        {
            switch (CurrentEncoderType)
            {
                case EncoderType.Render:
                    {
                        // afterStages may only name stages that can be waited on, which
                        // on Apple GPUs excludes fragment and tile: passing them makes
                        // the barrier illegal and its behaviour undefined, so writes it
                        // was meant to order can be read before they land.
                        // MTLBarrierScopeRenderTargets is not accepted by a render
                        // encoder barrier on this device either, and including it makes
                        // the whole barrier illegal.
                        MTLBarrierScope scope = MTLBarrierScope.Buffers | MTLBarrierScope.Textures;
                        Encoders.RenderEncoder.MemoryBarrier(
                            scope,
                            MTLRenderStages.RenderStageVertex,
                            MTLRenderStages.RenderStageVertex | MTLRenderStages.RenderStageFragment);
                        break;
                    }
                case EncoderType.Compute:
                    {
                        // RenderTargets scope is only valid on render encoders; passing
                        // it to a compute encoder is rejected by the validation layer
                        // ("scope has an invalid value for compute", 5500+ hits per
                        // minute in TOTK's Depths) and leaves the barrier behaviour
                        // undefined - compute results could be read before the writes
                        // completed, freezing compute-driven effects.
                        MTLBarrierScope scope = MTLBarrierScope.Buffers | MTLBarrierScope.Textures;
                        Encoders.ComputeEncoder.MemoryBarrier(scope);
                        break;
                    }
            }
        }

        public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
        {
            MTLBlitCommandEncoder blitCommandEncoder = GetOrCreateBlitEncoder();

            MTLBuffer mtlBuffer = _renderer.BufferManager.GetBuffer(destination, offset, size, true).Get(Cbs, offset, size, true).Value;

            // Might need a closer look, range's count, lower, and upper bound
            // must be a multiple of 4
            blitCommandEncoder.FillBuffer(mtlBuffer,
                new NSRange
                {
                    location = (ulong)offset,
                    length = (ulong)size
                },
                (byte)value);
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            float[] colors = [color.Red, color.Green, color.Blue, color.Alpha];
            Texture dst = _encoderStateManager.RenderTargets[index];

            // TODO: Remove workaround for Wonder which has an invalid texture due to unsupported format
            if (dst == null)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Attempted to clear invalid render target!");
                return;
            }

            // A draw-based clear: it opens a pass on the target and writes the whole
            // surface without going through the ordinary draw path, which is exactly the
            // p=1 d=0 entry the census reports for the texture the composite samples. If
            // that clear is white, a flat frame is the clear showing through.
            HdrPassProbe.NoteSceneClear(dst, color);

            _renderer.HelperShader.ClearColor(index, colors, componentMask, dst.Width, dst.Height, dst.Info.Format);
        }

        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            Texture depthStencil = _encoderStateManager.DepthStencil;

            if (depthStencil == null)
            {
                return;
            }

            _renderer.HelperShader.ClearDepthStencil(depthValue, depthMask, stencilValue, stencilMask, depthStencil.Width, depthStencil.Height);
        }

        public void CommandBufferBarrier()
        {
            Barrier();
        }

        public void CopyBuffer(BufferHandle src, BufferHandle dst, int srcOffset, int dstOffset, int size)
        {
            Auto<DisposableBuffer> srcBuffer = _renderer.BufferManager.GetBuffer(src, srcOffset, size, false);
            Auto<DisposableBuffer> dstBuffer = _renderer.BufferManager.GetBuffer(dst, dstOffset, size, true);

            BufferHolder.Copy(Cbs, srcBuffer, dstBuffer, srcOffset, dstOffset, size);
        }

        public void PushDebugGroup(string name)
        {
            MTLCommandEncoder? encoder = Encoders.CurrentEncoder;
            NSString debugGroupName = StringHelper.NSString(name);

            if (encoder == null)
            {
                return;
            }

            switch (Encoders.CurrentEncoderType)
            {
                case EncoderType.Render:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
                case EncoderType.Blit:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
                case EncoderType.Compute:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
            }
        }

        public void PopDebugGroup()
        {
            MTLCommandEncoder? encoder = Encoders.CurrentEncoder;

            if (encoder == null)
            {
                return;
            }

            switch (Encoders.CurrentEncoderType)
            {
                case EncoderType.Render:
                    encoder.Value.PopDebugGroup();
                    break;
                case EncoderType.Blit:
                    encoder.Value.PopDebugGroup();
                    break;
                case EncoderType.Compute:
                    encoder.Value.PopDebugGroup();
                    break;
            }
        }

        // The flip interval holds exactly two dispatches and a zero-draw pass. Skipping
        // dispatches by label splits them: the rate collapsing names the writer.
        // /tmp/ryujinx-metal-skip-dispatch holds comma-separated label prefixes.
        private static string[] _skipDispatchLabels = System.Array.Empty<string>();

        internal static void RefreshSkipDispatch()
        {
            try
            {
                _skipDispatchLabels = System.IO.File.Exists("/tmp/ryujinx-metal-skip-dispatch")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-dispatch")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : System.Array.Empty<string>();
            }
            catch (System.IO.IOException)
            {
            }
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            string computeLabel = _encoderStateManager.ComputeProgram?.DebugLabel;

            if (_skipDispatchLabels.Length != 0 && computeLabel != null)
            {
                foreach (string skip in _skipDispatchLabels)
                {
                    if (computeLabel.StartsWith(skip, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }

            OpRing.NoteDispatch(computeLabel);
            DispatchCompute(groupsX, groupsY, groupsZ, String.Empty);
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ, string debugGroupName)
        {
            MTLComputeCommandEncoder computeCommandEncoder = GetOrCreateComputeEncoder(true);

            ComputeSize localSize = _encoderStateManager.ComputeLocalSize;

            TraceDispatch(groupsX, groupsY, groupsZ, localSize);

            if (debugGroupName != String.Empty)
            {
                PushDebugGroup(debugGroupName);
            }

            computeCommandEncoder.DispatchThreadgroups(
                new MTLSize { width = (ulong)groupsX, height = (ulong)groupsY, depth = (ulong)groupsZ },
                new MTLSize { width = (ulong)localSize.X, height = (ulong)localSize.Y, depth = (ulong)localSize.Z });

            DispatchCount++;

            if (debugGroupName != String.Empty)
            {
                PopDebugGroup();
            }

            _encoderStateManager.DisposeComputeTemporaryBuffers();
        }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
        {
            Draw(vertexCount, instanceCount, firstVertex, firstInstance, String.Empty);
        }

        // Must run before any state or buffer is captured against the current
        // command buffer, since a flush swaps Cbs.
        private void AutoFlushPreDraw()
        {
            if (_renderer.AutoFlush.ShouldFlushDraw(DrawCount))
            {
                _autoFlushDrawCount++;
                FlushCommandsImpl();
            }

            DrawCount++;
        }

        // Gloom (miasma) material programs identified from the v41/v43 shader dumps:
        // their fragment shaders scroll volumetric noise by sin(fp_c4[9].z * PI), so
        // dumping their constant buffers across the traced frames reveals whether that
        // phase input advances per frame or is frozen. An extra label can be supplied
        // via RYUJINX_METAL_DUMP_CBUF for programs whose hash shifts with spec state.
        private static readonly string _extraCbufDumpLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_CBUF");

        private static bool IsGloomTraceProgram(string label)
        {
            switch (label)
            {
                case "ea7aeb577fe51e2f":
                case "b82634558e8e193d":
                case "6a81d5ed27684a34":
                case "375f2adfe7dbc34b":
                    return true;
                default:
                    return _extraCbufDumpLabel != null && label == _extraCbufDumpLabel;
            }
        }

        private static readonly string _dumpDepthAfter =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_DEPTH_AFTER");

        private static readonly int _dumpDepthNth =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_DEPTH_NTH"), out int n) ? n : 1;

        private bool _depthDumped;
        private int _depthDumpSeen;
        private bool _depthDumpPending;

        /// <summary>
        /// Reads the bound depth attachment back and reports how many pixels left the
        /// cleared value. <paramref name="when"/> tells the sample taken before the
        /// draw is encoded apart from the one taken after it.
        /// </summary>
        private void SampleDepth(string label, string when)
        {
            Texture depth = _encoderStateManager.DepthStencil;

            if (depth == null)
            {
                return;
            }

            EndCurrentPass(PassEndReason.DepthDump);
            _renderer.FlushAllCommands();

            using PinnedSpan<byte> data = depth.GetData();
            ReadOnlySpan<byte> bytes = data.Get();

            int total = bytes.Length / sizeof(float);
            int written = 0;
            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < total; i++)
            {
                float value = BitConverter.ToSingle(bytes.Slice(i * sizeof(float), sizeof(float)));

                if (value < 0.99999f)
                {
                    written++;
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }
            }

            string range = written != 0 ? $" min={min:F6} max={max:F6}" : string.Empty;

            Logger.Warning?.PrintMsg(
                LogClass.Gpu,
                $"depthdump {when} {label} draw#{_depthDumpSeen}: written={written}/{total}{range}");
        }

        /// <summary>
        /// Takes the "before" depth sample and arms the "after" one. The trace hook
        /// runs before the primitive is encoded, so a readback done from there alone
        /// reports the state the draw has not contributed to yet.
        /// </summary>
        private void DumpDepthAroundDraw(string label)
        {
            if (_dumpDepthAfter == null || _depthDumped || label != _dumpDepthAfter)
            {
                return;
            }

            if (++_depthDumpSeen < _dumpDepthNth)
            {
                return;
            }

            if (_encoderStateManager.DepthStencil == null)
            {
                return;
            }

            _depthDumped = true;
            _depthDumpPending = true;

            SampleDepth(label, "before");
        }

        /// <summary>
        /// Called once the primitive has been encoded, to take the "after" sample.
        /// </summary>
        private void TraceDrawPost()
        {
            if (!_depthDumpPending)
            {
                return;
            }

            _depthDumpPending = false;

            SampleDepth(_dumpDepthAfter, "after ");
        }

        // Diagnostic: RYUJINX_METAL_CAPTURE_FROM / _TO name the programs whose draws
        // open and close the GPU capture scope, so a trace covers only them.
        private static readonly string _captureFromLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_FROM");

        private static readonly string _captureToLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_TO");

        private bool _scopeOpen;
        private bool _scopeClosed;

        private void TraceDraw(string kind, int count, int instanceCount, int firstIndexOrVertex, int firstInstance)
        {
            if (_renderer.FrameCapture.DrawTraceActive)
            {
                Texture target = _encoderStateManager.RenderTargets[0];
                string targetText = target != null ? $"{target.Width}x{target.Height}/{target.Info.Format}" : "none";

                Program program = _encoderStateManager.RenderProgram;
                string programText = "none";

                if (program != null)
                {
                    programText = program.DebugLabel;
                    program.DumpSources(FrameCapture.ShaderDumpDir);
                }

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"trace draw#{DrawCount} {kind} count={count} inst={instanceCount} first={firstIndexOrVertex} firstInst={firstInstance} topo={_encoderStateManager.Topology} rt={targetText} prog={programText}");

                if (instanceCount > 1 && kind == "Draw")
                {
                    _encoderStateManager.TraceDumpInstanceBuffers(firstInstance);
                }

                DumpDepthAroundDraw(programText);

                if (_captureFromLabel != null && programText == _captureFromLabel && !_scopeOpen &&
                    _renderer.FrameCapture.ScopeReady)
                {
                    _scopeOpen = true;
                    _renderer.FrameCapture.BeginScope();
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"capture scope opened at {programText}");
                }
                else if (_captureToLabel != null && programText == _captureToLabel && _scopeOpen && !_scopeClosed)
                {
                    _scopeClosed = true;
                    _renderer.FrameCapture.EndScope();
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"capture scope closed at {programText}");
                }

                if (IsGloomTraceProgram(programText))
                {
                    _encoderStateManager.TraceDumpUniformBuffers(programText);
                    _encoderStateManager.TraceDumpTextures(programText);
                }
            }
        }

        private void TraceDispatch(int groupsX, int groupsY, int groupsZ, ComputeSize localSize)
        {
            if (_renderer.FrameCapture.DrawTraceActive)
            {
                Program program = _encoderStateManager.ComputeProgram;
                string programText = "none";

                if (program != null)
                {
                    programText = program.DebugLabel;
                    program.DumpSources(FrameCapture.ShaderDumpDir);
                }

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"trace dispatch groups={groupsX}x{groupsY}x{groupsZ} local={localSize.X}x{localSize.Y}x{localSize.Z} prog={programText}");
            }
        }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance, string debugGroupName)
        {
            if (vertexCount == 0)
            {
                return;
            }

            AutoFlushPreDraw();

            if (_hardSync && _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel == "3ebc3a8f6b77cc8f")
            {
                CommandBufferScoped previous = Cbs;
                FlushCommandsImpl();
                previous.CommandBuffer.WaitUntilCompleted();
            }

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.NoteDraw(_encoderStateManager.RenderTargets[0],
                    _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel, DrawCount);

                bool isToneDraw = HdrPassProbe.TakeToneDrawFlag();

                if (isToneDraw && HdrPassProbe.ShouldSkipToneDraw())
                {
                    return;
                }

                if (HdrPassProbe.ShouldSkipDraw(_encoderStateManager.RenderTargets[0]) ||
                    HdrPassProbe.ShouldSkipPresentWrite(_encoderStateManager.RenderTargets[0]))
                {
                    return;
                }
            }

            if (DrawRing.Enabled)
            {
                DrawRing.Record(_encoderStateManager.CurrentEncoderState, _encoderStateManager.Topology, vertexCount, instanceCount);
            }

            TraceDraw("Draw", vertexCount, instanceCount, firstVertex, firstInstance);

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();

            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                IndexBufferPattern pattern = GetIndexBufferPattern();

                BufferHandle handle = pattern.GetRepeatingBuffer(vertexCount, out int indexCount);
                Auto<DisposableBuffer> buffer = _renderer.BufferManager.GetBuffer(handle, false);
                MTLBuffer mtlBuffer = buffer.Get(Cbs, 0, indexCount * sizeof(int)).Value;

                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);
                ToneMapProbe.Record(_encoderStateManager.CurrentEncoderState);

                // Checked only after the encoder has applied state, since that is what
                // builds the pipeline. A failed build leaves the encoder without a usable
                // pipeline, and issuing the draw anyway faults inside the Metal driver.
                // Only the draw is skipped - the cleanup below still has to run.
                if (_encoderStateManager.HasValidRenderPipeline &&
                    !(_skipHdrDraws && HdrPassProbe.IsWatchedTarget(_encoderStateManager.RenderTargets[0])) &&
                    !(_skipShader.Length != 0 &&
                      _skipShader == _encoderStateManager.RenderProgram?.DebugLabel))
                {
                    // The converted-topology path must keep the draw's instancing and
                    // base vertex/instance: dropping them draws a single instance of
                    // the wrong vertices for instanced quad/fan draws.
                    renderCommandEncoder.DrawIndexedPrimitives(
                        primitiveType,
                        (ulong)indexCount,
                        MTLIndexType.UInt32,
                        mtlBuffer,
                        0,
                        (ulong)instanceCount,
                        firstVertex,
                        (ulong)firstInstance);
                }
            }
            else
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);
                ToneMapProbe.Record(_encoderStateManager.CurrentEncoderState);

                if (debugGroupName != String.Empty)
                {
                    PushDebugGroup(debugGroupName);
                }

                if (_encoderStateManager.HasValidRenderPipeline &&
                    !(_skipHdrDraws && HdrPassProbe.IsWatchedTarget(_encoderStateManager.RenderTargets[0])) &&
                    !(_skipShader.Length != 0 &&
                      _skipShader == _encoderStateManager.RenderProgram?.DebugLabel))
                {
                    renderCommandEncoder.DrawPrimitives(
                        primitiveType,
                        (ulong)firstVertex,
                        (ulong)vertexCount,
                        (ulong)instanceCount,
                        (ulong)firstInstance);
                }

                if (debugGroupName != String.Empty)
                {
                    PopDebugGroup();
                }
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();

            if (_stainSweep && _stainAtDraw >= 0 && _lastPresentSource != null &&
                (int)(DrawCount - _drawCountAtFrameStart) == _stainAtDraw)
            {
                EndCurrentPass(PassEndReason.Unspecified);

                MTLRenderPassDescriptor sweep = new();
                MTLRenderPassColorAttachmentDescriptor sa = sweep.ColorAttachments.Object(0);
                sa.Texture = _lastPresentSource.GetIdentityHandle(Cbs);
                sa.LoadAction = MTLLoadAction.Clear;
                sa.StoreAction = MTLStoreAction.Store;
                sa.ClearColor = new MTLClearColor { red = 0.0, green = 1.0, blue = 0.0, alpha = 1.0 };

                MTLRenderCommandEncoder sweepEncoder = CommandBuffer.RenderCommandEncoder(sweep);
                sweepEncoder.EndEncoding();
                sweep.Dispose();
            }

            if (_serializeDraws)
            {
                EndCurrentPass(PassEndReason.FragmentDependency);
            }

            TraceDrawPost();
        }

        private IndexBufferPattern GetIndexBufferPattern()
        {
            return _encoderStateManager.Topology switch
            {
                PrimitiveTopology.Quads => QuadsToTrisPattern,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => TriFanToTrisPattern,
                _ => throw new NotSupportedException($"Unsupported topology: {_encoderStateManager.Topology}"),
            };
        }

        private PrimitiveTopology TopologyRemap(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => PrimitiveTopology.Triangles,
                PrimitiveTopology.QuadStrip => PrimitiveTopology.TriangleStrip,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => PrimitiveTopology.Triangles,
                _ => topology,
            };
        }

        private bool TopologyUnsupported(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads or PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => true,
                _ => false,
            };
        }

        public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
        {
            if (indexCount == 0)
            {
                return;
            }

            AutoFlushPreDraw();

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.NoteDraw(_encoderStateManager.RenderTargets[0],
                    _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel, DrawCount);

                bool isToneDraw = HdrPassProbe.TakeToneDrawFlag();

                if (isToneDraw && HdrPassProbe.ShouldSkipToneDraw())
                {
                    return;
                }

                if (HdrPassProbe.ShouldSkipDraw(_encoderStateManager.RenderTargets[0]) ||
                    HdrPassProbe.ShouldSkipPresentWrite(_encoderStateManager.RenderTargets[0]))
                {
                    return;
                }
            }

            if (DrawRing.Enabled)
            {
                DrawRing.Record(_encoderStateManager.CurrentEncoderState, _encoderStateManager.Topology, indexCount, instanceCount);
            }

            TraceDraw("DrawIndexed", indexCount, instanceCount, firstIndex, firstInstance);

            MTLBuffer mtlBuffer;
            int offset;
            MTLIndexType type;
            int finalIndexCount = indexCount;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();

            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                IndexBufferPattern pattern = GetIndexBufferPattern();
                int convertedCount = pattern.GetConvertedCount(indexCount);

                finalIndexCount = convertedCount;

                (mtlBuffer, offset, type) = _encoderStateManager.IndexBuffer.GetConvertedIndexBuffer(_renderer, Cbs, firstIndex, indexCount, convertedCount, pattern);
            }
            else
            {
                (mtlBuffer, offset, type) = _encoderStateManager.IndexBuffer.GetIndexBuffer(_renderer, Cbs);
            }

            if (mtlBuffer.NativePtr != IntPtr.Zero)
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);
                ToneMapProbe.Record(_encoderStateManager.CurrentEncoderState);

                if (_encoderStateManager.HasValidRenderPipeline &&
                    !(_skipHdrDraws && HdrPassProbe.IsWatchedTarget(_encoderStateManager.RenderTargets[0])) &&
                    !(_skipShader.Length != 0 &&
                      _skipShader == _encoderStateManager.RenderProgram?.DebugLabel))
                {
                    renderCommandEncoder.DrawIndexedPrimitives(
                        primitiveType,
                        (ulong)finalIndexCount,
                        type,
                        mtlBuffer,
                        (ulong)offset,
                        (ulong)instanceCount,
                        firstVertex,
                        (ulong)firstInstance);
                }
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();

            if (_stainSweep && _stainAtDraw >= 0 && _lastPresentSource != null &&
                (int)(DrawCount - _drawCountAtFrameStart) == _stainAtDraw)
            {
                EndCurrentPass(PassEndReason.Unspecified);

                MTLRenderPassDescriptor sweep = new();
                MTLRenderPassColorAttachmentDescriptor sa = sweep.ColorAttachments.Object(0);
                sa.Texture = _lastPresentSource.GetIdentityHandle(Cbs);
                sa.LoadAction = MTLLoadAction.Clear;
                sa.StoreAction = MTLStoreAction.Store;
                sa.ClearColor = new MTLClearColor { red = 0.0, green = 1.0, blue = 0.0, alpha = 1.0 };

                MTLRenderCommandEncoder sweepEncoder = CommandBuffer.RenderCommandEncoder(sweep);
                sweepEncoder.EndEncoding();
                sweep.Dispose();
            }

            if (_serializeDraws)
            {
                EndCurrentPass(PassEndReason.FragmentDependency);
            }

            TraceDrawPost();
        }

        public void DrawIndexedIndirect(BufferRange indirectBuffer)
        {
            DrawIndexedIndirectOffset(indirectBuffer);
        }

        public void DrawIndexedIndirectOffset(BufferRange indirectBuffer, int offset = 0)
        {
            // TODO: Reindex unsupported topologies
            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Drawing indexed with unsupported topology: {_encoderStateManager.Topology}");
            }

            AutoFlushPreDraw();

            TraceDraw("DrawIndexedIndirect", 0, 0, offset, 0);

            MTLBuffer buffer = _renderer.BufferManager
                .GetBuffer(indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();

            (MTLBuffer indexBuffer, int indexOffset, MTLIndexType type) = _encoderStateManager.IndexBuffer.GetIndexBuffer(_renderer, Cbs);

            if (indexBuffer.NativePtr != IntPtr.Zero && buffer.NativePtr != IntPtr.Zero)
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                if (_encoderStateManager.HasValidRenderPipeline &&
                    !(_skipHdrDraws && HdrPassProbe.IsWatchedTarget(_encoderStateManager.RenderTargets[0])) &&
                    !(_skipShader.Length != 0 &&
                      _skipShader == _encoderStateManager.RenderProgram?.DebugLabel))
                {
                    renderCommandEncoder.DrawIndexedPrimitives(
                        primitiveType,
                        type,
                        indexBuffer,
                        (ulong)indexOffset,
                        buffer,
                        (ulong)(indirectBuffer.Offset + offset));
                }
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();
        }

        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            for (int i = 0; i < maxDrawCount; i++)
            {
                DrawIndexedIndirectOffset(indirectBuffer, stride * i);
            }
        }

        public void DrawIndirect(BufferRange indirectBuffer)
        {
            DrawIndirectOffset(indirectBuffer);
        }

        public void DrawIndirectOffset(BufferRange indirectBuffer, int offset = 0)
        {
            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                // TODO: Reindex unsupported topologies
                Logger.Warning?.Print(LogClass.Gpu, $"Drawing indirect with unsupported topology: {_encoderStateManager.Topology}");
            }

            AutoFlushPreDraw();

            TraceDraw("DrawIndirect", 0, 0, offset, 0);

            MTLBuffer buffer = _renderer.BufferManager
                .GetBuffer(indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();
            MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

            if (_encoderStateManager.HasValidRenderPipeline)
            {
                renderCommandEncoder.DrawPrimitives(
                    primitiveType,
                    buffer,
                    (ulong)(indirectBuffer.Offset + offset));
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();
        }

        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            for (int i = 0; i < maxDrawCount; i++)
            {
                DrawIndirectOffset(indirectBuffer, stride * i);
            }
        }

        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            _renderer.HelperShader.DrawTexture(texture, sampler, srcRegion, dstRegion);
        }

        public void SetAlphaTest(bool enable, float reference, CompareOp op)
        {
            // This is currently handled using shader specialization, as Metal does not support alpha test.
            // In the future, we may want to use this to write the reference value into the support buffer,
            // to avoid creating one version of the shader per reference value used.
        }

        public void SetBlendState(AdvancedBlendDescriptor blend)
        {
            // Metal does not support advanced blend.
        }

        public void SetBlendState(int index, BlendDescriptor blend)
        {
            _encoderStateManager.UpdateBlendDescriptors(index, blend);
        }

        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
        {
            if (enables == 0)
            {
                _encoderStateManager.UpdateDepthBias(0, 0, 0);
            }
            else
            {
                _encoderStateManager.UpdateDepthBias(units, factor, clamp);
            }
        }

        public void SetDepthClamp(bool clamp)
        {
            _encoderStateManager.UpdateDepthClamp(clamp);
        }

        public void SetDepthMode(DepthMode mode)
        {
            // Metal does not support depth clip control.
        }

        public void SetDepthTest(DepthTestDescriptor depthTest)
        {
            _encoderStateManager.UpdateDepthState(depthTest);
        }

        public void SetFaceCulling(bool enable, Face face)
        {
            _encoderStateManager.UpdateCullMode(enable, face);
        }

        public void SetFrontFace(FrontFace frontFace)
        {
            _encoderStateManager.UpdateFrontFace(frontFace);
        }

        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _encoderStateManager.UpdateIndexBuffer(buffer, type);
        }

        public void SetImage(ShaderStage stage, int binding, ITexture image)
        {
            if (image is TextureBase img)
            {
                _encoderStateManager.UpdateImage(stage, binding, img);
            }
        }

        public void SetImageArray(ShaderStage stage, int binding, IImageArray array)
        {
            if (array is ImageArray imageArray)
            {
                _encoderStateManager.UpdateImageArray(stage, binding, imageArray);
            }
        }

        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
        {
            if (array is ImageArray imageArray)
            {
                _encoderStateManager.UpdateImageArraySeparate(stage, setIndex, imageArray);
            }
        }

        public void SetLineParameters(float width, bool smooth)
        {
            // Metal does not support wide-lines.
        }

        public void SetLogicOpState(bool enable, LogicalOp op)
        {
            _encoderStateManager.UpdateLogicOpState(enable, op);
        }

        public void SetMultisampleState(MultisampleDescriptor multisample)
        {
            _encoderStateManager.UpdateMultisampleState(multisample);
        }

        public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
        {
            // TODO: Default tessellation levels need shader emulation.
        }

        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
        {
            // TODO: Point size and point sprites need shader emulation.
        }

        public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode)
        {
            // Metal does not support polygon mode.
        }

        public void SetPrimitiveRestart(bool enable, int index)
        {
            // Always active for LineStrip and TriangleStrip
            // https://github.com/gpuweb/gpuweb/issues/1220#issuecomment-732483263
            // https://developer.apple.com/documentation/metal/mtlrendercommandencoder/1515520-drawindexedprimitives
            // https://stackoverflow.com/questions/70813665/how-to-render-multiple-trianglestrips-using-metal

            // Emulating disabling this is very difficult. It's unlikely for an index buffer to use the largest possible index,
            // so it's fine nearly all of the time.
        }

        public void SetPrimitiveTopology(PrimitiveTopology topology)
        {
            _encoderStateManager.UpdatePrimitiveTopology(topology);
        }

        public void SetProgram(IProgram program)
        {
            _encoderStateManager.UpdateProgram(program);
        }

        public void SetRasterizerDiscard(bool discard)
        {
            _encoderStateManager.UpdateRasterizerDiscard(discard);
        }

        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            _encoderStateManager.UpdateRenderTargetColorMasks(componentMask);
        }

        public void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil)
        {
            // Attachment changes end the current render pass anyway, so they are the
            // cheapest place to submit pending work early for host sync waits.
            if (_renderer.AutoFlush.ShouldFlushAttachmentChange(DrawCount))
            {
                _autoFlushAttachmentCount++;
                FlushCommandsImpl();
            }

            _encoderStateManager.UpdateRenderTargets(colors, depthStencil);
        }

        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            _encoderStateManager.UpdateScissors(regions);
        }

        public void SetStencilTest(StencilTestDescriptor stencilTest)
        {
            _encoderStateManager.UpdateStencilState(stencilTest);
        }

        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _encoderStateManager.UpdateUniformBuffers(buffers);
        }

        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _encoderStateManager.UpdateStorageBuffers(buffers);
        }

        internal void SetStorageBuffers(int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
        {
            _encoderStateManager.UpdateStorageBuffers(first, buffers);
        }

        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            if (texture is TextureBase tex)
            {
                if (sampler == null || sampler is SamplerHolder)
                {
                    _encoderStateManager.UpdateTextureAndSampler(stage, binding, tex, (SamplerHolder)sampler);
                }
            }
        }

        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
        {
            if (array is TextureArray textureArray)
            {
                _encoderStateManager.UpdateTextureArray(stage, binding, textureArray);
            }
        }

        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
        {
            if (array is TextureArray textureArray)
            {
                _encoderStateManager.UpdateTextureArraySeparate(stage, setIndex, textureArray);
            }
        }

        public void SetUserClipDistance(int index, bool enableClip)
        {
            // TODO. Same as Vulkan
        }

        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            _encoderStateManager.UpdateVertexAttribs(vertexAttribs);
        }

        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            _encoderStateManager.UpdateVertexBuffers(vertexBuffers);
        }

        public void SetViewports(ReadOnlySpan<Viewport> viewports)
        {
            _encoderStateManager.UpdateViewports(viewports);
        }

        // A/B switch for the skip below, re-read once a frame so both behaviours can be
        // measured inside one session - this machine's white-frame rate swings with the
        // camera, so comparing separate runs measures the view, not the change.
        private static bool _strictBarrier;

        // Diagnostic hammer: end the render pass after every draw, so no draw can ever
        // read a texture another draw in the same pass wrote. If the white frames
        // survive full serialisation the fault cannot be a read-after-write ordering
        // problem, and has to be inside the shader itself.
        private static bool _serializeDraws;

        // Bisect: drop every draw whose colour target 0 is the full resolution composite.
        // If the frame still comes out flat white with all of them gone, the white was
        // already in that texture when the passes loaded it, and nothing they draw is
        // responsible - which is the half of the search space no instrument here has been
        // able to separate. Hot-swappable so both arms run in one session.
        private static bool _skipHdrDraws;

        // Per shader bisect. Every externally observable quantity matches between flat and
        // ordinary frames, so the difference is inside shader execution; dropping one
        // shader's draws at a time is what names which one. Hot-swappable, so all arms run
        // in the session that reproduces the fault.
        private static string _skipShader = string.Empty;

        // Diagnostic hammer, stronger than _serializeDraws: commit the command buffer and
        // block until the GPU has finished it before the watched draw. That orders the
        // watched read after every write already recorded, across encoders AND across
        // command buffers. If the white frames survive this, no ordering fix can help -
        // the shader is being handed the wrong contents, not stale ones.
        private static bool _hardSync;
        private static bool _stainPresentSource;
        private static bool _stainBeforePresent;

        // Sweep: stain the present source after the Kth draw of the frame, with K stepping
        // frame by frame. The frame comes back green only if nothing wrote the source
        // after draw K, so the K where green stops is the last writer's position. One run
        // covers the whole range, which matters because the trigger view lasts ~2 minutes.
        private static bool _stainSweep;
        private static int _stainAtDraw = -1;
        private static ulong _drawCountAtFrameStart;
        private Texture _lastPresentSource;

        // The surface presented last time. The present source alternates between a couple
        // of textures, so the other one already holds the previous frame - no copy needed.
        private Texture _guardPreviousSource;

        internal static void RefreshBarrierToggle()
        {
            try
            {
                _strictBarrier = System.IO.File.Exists("/tmp/ryujinx-metal-strict-barrier") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-strict-barrier").Trim() == "1";

                _serializeDraws = System.IO.File.Exists("/tmp/ryujinx-metal-serialize") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-serialize").Trim() == "1";

                _skipHdrDraws = System.IO.File.Exists("/tmp/ryujinx-metal-skip-hdr") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-hdr").Trim() == "1";

                _skipShader = System.IO.File.Exists("/tmp/ryujinx-metal-skip-shader")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-shader").Trim()
                    : string.Empty;

                _hardSync = System.IO.File.Exists("/tmp/ryujinx-metal-hardsync") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-hardsync").Trim() == "1";

                HdrPassProbe.SetSkipToneDraw(System.IO.File.Exists("/tmp/ryujinx-metal-skiptone") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-skiptone").Trim() == "1");

                HdrPassProbe.SetSkipPresentWrites(System.IO.File.Exists("/tmp/ryujinx-metal-skippresent") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-skippresent").Trim() == "1");

                _stainPresentSource = System.IO.File.Exists("/tmp/ryujinx-metal-stain") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-stain").Trim() == "1";

                _stainBeforePresent = System.IO.File.Exists("/tmp/ryujinx-metal-stainpre") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-stainpre").Trim() == "1";

                _stainSweep = System.IO.File.Exists("/tmp/ryujinx-metal-stainsweep") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-stainsweep").Trim() == "1";

                if (System.IO.File.Exists("/tmp/ryujinx-metal-skiprange"))
                {
                    string[] parts = System.IO.File.ReadAllText("/tmp/ryujinx-metal-skiprange").Trim().Split(' ');

                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], out int lo) &&
                        int.TryParse(parts[1], out int hi))
                    {
                        HdrPassProbe.SetSkipRange(lo, hi);
                    }
                }
                else
                {
                    HdrPassProbe.SetSkipRange(-1, -1);
                }
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        public void TextureBarrier()
        {
            if (CurrentEncoderType != EncoderType.Render)
            {
                return;
            }

            // The barrier orders fragment writes before later fragment reads. Writes
            // made by earlier passes are already ordered by the boundary that ended
            // them, so with no draw encoded since this pass began there is nothing
            // for it to order - and splitting anyway costs a full attachment store
            // and reload. The guest issues these in the hundreds per frame.
            if (!_strictBarrier && DrawCount == _drawCountAtPassStart)
            {
                _passEndReasons[(int)PassEndReason.FragmentDependencySkipped]++;

                return;
            }

            // A fragment-writes-then-fragment-reads dependency cannot be expressed
            // as a render encoder barrier on Apple GPUs (afterStages must not name
            // fragment). Ending the pass is the only construct that actually orders
            // the two, and an illegal barrier here orders nothing at all.
            EndCurrentPass(PassEndReason.FragmentDependency);
        }

        public void TextureBarrierTiled()
        {
            TextureBarrier();
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            // TODO: Implementable via indirect draw commands
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            // TODO: Implementable via indirect draw commands
            return false;
        }

        public void EndHostConditionalRendering()
        {
            // TODO: Implementable via indirect draw commands
        }

        public void BeginTransformFeedback(PrimitiveTopology topology)
        {
            // Metal does not support transform feedback.
        }

        public void EndTransformFeedback()
        {
            // Metal does not support transform feedback.
        }

        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
        {
            // Metal does not support transform feedback.
        }

        public void Dispose()
        {
            EndCurrentPass(PassEndReason.Dispose);
            _encoderStateManager.Dispose();
        }
    }
}
