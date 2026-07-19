using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.QuartzCore;
using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
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
        private int _autoFlushDrawCount;
        private int _autoFlushAttachmentCount;

        public ulong DrawCount { get; private set; }

        public MTLCommandBuffer CommandBuffer;

        public IndexBufferPattern QuadsToTrisPattern;
        public IndexBufferPattern TriFanToTrisPattern;

        internal CommandBufferScoped? PreloadCbs { get; private set; }
        internal CommandBufferScoped Cbs { get; private set; }
        internal CommandBufferEncoder Encoders => Cbs.Encoders;
        internal EncoderType CurrentEncoderType => Encoders.CurrentEncoderType;

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
                EndCurrentPass();
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

            if (forDraw)
            {
                _encoderStateManager.RenderResourcesPrepass();
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

        public void EndCurrentPass()
        {
            Cbs.Encoders.EndCurrentPass();
        }

        public MTLRenderCommandEncoder CreateRenderCommandEncoder()
        {
            return _encoderStateManager.CreateRenderCommandEncoder();
        }

        public MTLComputeCommandEncoder CreateComputeCommandEncoder()
        {
            return _encoderStateManager.CreateComputeCommandEncoder();
        }

        public void Present(CAMetalDrawable drawable, Texture src, Extents2D srcRegion, Extents2D dstRegion, bool isLinear, bool useFsrSharpener, float scalingFilterLevel)
        {
            _renderer.FrameCapture.OnPresentBegin();

            // TODO: Clean this up
            TextureCreateInfo textureInfo = new((int)drawable.Texture.Width, (int)drawable.Texture.Height, (int)drawable.Texture.Depth, (int)drawable.Texture.MipmapLevelCount, (int)drawable.Texture.SampleCount, 0, 0, 0, Format.B8G8R8A8Unorm, 0, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
            Texture dst = new(_device, _renderer, this, textureInfo, drawable.Texture, 0, 0);

            if (useFsrSharpener)
            {
                _renderer.HelperShader.PresentColor(Cbs, src, dst, srcRegion, dstRegion, scalingFilterLevel / 100f, true);
            }
            else
            {
                _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, isLinear, true);
            }

            EndCurrentPass();

            Cbs.CommandBuffer.PresentDrawable(drawable);

            FlushCommandsImpl();

            _renderer.AutoFlush.Present();
            _renderer.FrameCapture.ProcessPresent();

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
                    out string waitDurations);

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

                    Logger.Info?.PrintMsg(
                        LogClass.Gpu,
                        $"Metal sync stats over last {SyncStatsLogFrameInterval} frames: {waitMs:F2}ms in {syncWaitCount} waits, " +
                        $"{forcedSyncFlushCount} forced flushes, {proactiveSyncFlushCount} proactive flushes, " +
                        $"{coalescedSyncSignalCount} coalesced signals, " +
                        $"{autoFlushDrawCount} draw auto-flushes, {autoFlushAttachmentCount} attachment auto-flushes " +
                        $"(fast flush: {_renderer.AutoFlush.FastFlushMode}).{sourceText}{createText}{durationText}");
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
            EndCurrentPass();

            _byteWeight = 0;
            _disposedResourceCount = 0;

            if (PreloadCbs != null)
            {
                PreloadCbs.Value.Dispose();
                PreloadCbs = null;
            }

            CommandBuffer = (Cbs = _renderer.CommandBufferPool.ReturnAndRent(Cbs)).CommandBuffer;
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
                        MTLBarrierScope scope = MTLBarrierScope.Buffers | MTLBarrierScope.Textures | MTLBarrierScope.RenderTargets;
                        MTLRenderStages stages = MTLRenderStages.RenderStageVertex | MTLRenderStages.RenderStageFragment;
                        Encoders.RenderEncoder.MemoryBarrier(scope, stages, stages);
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

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
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

        private void TraceDraw(string kind, int count, int instanceCount, int firstIndexOrVertex)
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
                    $"trace draw#{DrawCount} {kind} count={count} inst={instanceCount} first={firstIndexOrVertex} rt={targetText} prog={programText}");
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
            TraceDraw("Draw", vertexCount, instanceCount, firstVertex);

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();

            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                IndexBufferPattern pattern = GetIndexBufferPattern();

                BufferHandle handle = pattern.GetRepeatingBuffer(vertexCount, out int indexCount);
                Auto<DisposableBuffer> buffer = _renderer.BufferManager.GetBuffer(handle, false);
                MTLBuffer mtlBuffer = buffer.Get(Cbs, 0, indexCount * sizeof(int)).Value;

                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                renderCommandEncoder.DrawIndexedPrimitives(
                    primitiveType,
                    (ulong)indexCount,
                    MTLIndexType.UInt32,
                    mtlBuffer,
                    0);
            }
            else
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                if (debugGroupName != String.Empty)
                {
                    PushDebugGroup(debugGroupName);
                }

                renderCommandEncoder.DrawPrimitives(
                    primitiveType,
                    (ulong)firstVertex,
                    (ulong)vertexCount,
                    (ulong)instanceCount,
                    (ulong)firstInstance);

                if (debugGroupName != String.Empty)
                {
                    PopDebugGroup();
                }
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();
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
            TraceDraw("DrawIndexed", indexCount, instanceCount, firstIndex);

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

            _encoderStateManager.DisposeRenderTemporaryBuffers();
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
            TraceDraw("DrawIndexedIndirect", 0, 0, offset);

            MTLBuffer buffer = _renderer.BufferManager
                .GetBuffer(indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();

            (MTLBuffer indexBuffer, int indexOffset, MTLIndexType type) = _encoderStateManager.IndexBuffer.GetIndexBuffer(_renderer, Cbs);

            if (indexBuffer.NativePtr != IntPtr.Zero && buffer.NativePtr != IntPtr.Zero)
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                renderCommandEncoder.DrawIndexedPrimitives(
                    primitiveType,
                    type,
                    indexBuffer,
                    (ulong)indexOffset,
                    buffer,
                    (ulong)(indirectBuffer.Offset + offset));
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
            TraceDraw("DrawIndirect", 0, 0, offset);

            MTLBuffer buffer = _renderer.BufferManager
                .GetBuffer(indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();
            MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

            renderCommandEncoder.DrawPrimitives(
                primitiveType,
                buffer,
                (ulong)(indirectBuffer.Offset + offset));

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

        public void TextureBarrier()
        {
            if (CurrentEncoderType == EncoderType.Render)
            {
                Encoders.RenderEncoder.MemoryBarrier(MTLBarrierScope.Textures, MTLRenderStages.RenderStageFragment, MTLRenderStages.RenderStageFragment);
            }
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
            EndCurrentPass();
            _encoderStateManager.Dispose();
        }
    }
}
