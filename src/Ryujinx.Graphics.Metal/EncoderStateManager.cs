using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.State;
using Ryujinx.Graphics.Metal.SharpMetalExtensions;
using Ryujinx.Graphics.Shader;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BufferAssignment = Ryujinx.Graphics.GAL.BufferAssignment;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// What a render command encoder has actually been told, as opposed to what the
    /// next draw wants. The dirty flags describe changes to the desired state, and
    /// several of them - the pipeline flag above all - are raised by updates that
    /// leave the encoder level state identical, so applying them unconditionally
    /// repeats the same call over and over within a pass.
    ///
    /// Held by reference so the readonly setters can update it, and keyed on the
    /// encoder itself: Metal starts every encoder from default state rather than
    /// from the previous encoder's, so a different encoder invalidates everything
    /// here without any dirty flag bookkeeping having to be right.
    /// </summary>
    [SupportedOSPlatform("macos")]
    sealed class AppliedRenderState
    {
        [Flags]
        public enum Field
        {
            Pipeline = 1 << 0,
            BlendColor = 1 << 1,
            DepthStencil = 1 << 2,
            DepthClip = 1 << 3,
            DepthBias = 1 << 4,
            Cull = 1 << 5,
            Winding = 1 << 6,
            StencilRef = 1 << 7,
        }

        // A/B switch: RYUJINX_METAL_STATE_CACHE=0, or a 0 in the toggle file, reports
        // every field as unknown, so each setter applies unconditionally the way it
        // did before this cache existed.
        //
        // The toggle is re-read once a frame because this machine's frame rate drifts
        // by ~10% over the twenty minutes three separate runs take, which is several
        // times the effect being measured. Alternating both behaviours inside one
        // session cancels that drift. Flipping is safe at any moment: the cache is
        // updated whether or not it is being consulted, so it never goes stale.
        // Levels, so a measurement can attribute the effect to one part at a time:
        // 0 nothing, 1 state fields, 2 also buffer binds, 3 also residency.
        public const int LevelOff = 0;
        public const int LevelFields = 1;
        public const int LevelBuffers = 2;
        public const int LevelResidency = 3;

        private const string ToggleFile = "/tmp/ryujinx-metal-state-cache";

        private static int _level = ParseLevel(Environment.GetEnvironmentVariable("RYUJINX_METAL_STATE_CACHE"), LevelResidency);

        private static int ParseLevel(string text, int fallback)
        {
            return int.TryParse(text, out int level) ? Math.Clamp(level, LevelOff, LevelResidency) : fallback;
        }

        public static void RefreshToggle()
        {
            try
            {
                if (File.Exists(ToggleFile))
                {
                    _level = ParseLevel(File.ReadAllText(ToggleFile).Trim(), _level);
                }
            }
            catch (IOException)
            {
                // Raced with the writer; the next frame will pick it up.
            }
        }

        private const int BufferSlots = (int)Constants.MaximumBufferArgumentTableEntries;
        private const int MaxResidentTracked = 8192;

        private IntPtr _encoder;
        private long _generation = -1;
        private Field _known;

        private readonly (IntPtr Buffer, ulong Offset)[] _vertexBuffers = new (IntPtr, ulong)[BufferSlots];
        private readonly (IntPtr Buffer, ulong Offset)[] _fragmentBuffers = new (IntPtr, ulong)[BufferSlots];
        private uint _vertexBound;
        private uint _fragmentBound;

        private readonly HashSet<(IntPtr Resource, MTLResourceUsage Usage, MTLRenderStages Stages)> _resident = [];

        public IntPtr PipelineState;

        /// <summary>
        /// Whether the last <see cref="EncoderStateManager.SetRenderPipelineState"/> left a
        /// usable pipeline on the encoder. Kept separate from <see cref="PipelineState"/>,
        /// which persists across encoders: zeroing that field to signal failure would make
        /// the draw guard skip the very call that repairs it, blacking out every later frame.
        /// </summary>
        public bool PipelineValid;

        public IntPtr DepthStencilState;
        public ColorF BlendColor;
        public MTLDepthClipMode DepthClipMode;
        public float DepthBias;
        public float SlopeScale;
        public float Clamp;
        public MTLCullMode CullMode;
        public MTLWinding Winding;
        public int FrontRefValue;
        public int BackRefValue;

        /// <summary>
        /// Whether this cache already holds the value <paramref name="field"/> was
        /// last set to on <paramref name="encoder"/>. False means the caller must
        /// apply it; the field is marked known either way, because every caller
        /// applies when this returns false.
        /// </summary>
        public bool Knows(MTLRenderCommandEncoder encoder, Field field)
        {
            Retarget(encoder);

            bool known = _level >= LevelFields && (_known & field) != 0;

            _known |= field;

            return known;
        }

        /// <summary>
        /// Whether <paramref name="buffer"/> is already bound at its own index for
        /// the vertex or fragment stage of this encoder, in which case binding it
        /// again would do nothing. Contents may of course have changed since - the
        /// binding names the buffer, and the GPU reads it when the draw executes.
        /// </summary>
        public bool IsBound(MTLRenderCommandEncoder encoder, in BufferResource buffer, bool fragment)
        {
            Retarget(encoder);

            if (buffer.Binding >= BufferSlots)
            {
                return false;
            }

            int slot = (int)buffer.Binding;

            ref (IntPtr Buffer, ulong Offset) bound = ref (fragment ? ref _fragmentBuffers[slot] : ref _vertexBuffers[slot]);
            ref uint mask = ref (fragment ? ref _fragmentBound : ref _vertexBound);

            uint bit = 1u << slot;

            bool same = _level >= LevelBuffers &&
                (mask & bit) != 0 &&
                bound.Buffer == buffer.Buffer.NativePtr &&
                bound.Offset == buffer.Offset;

            mask |= bit;
            bound = (buffer.Buffer.NativePtr, buffer.Offset);

            return same;
        }

        /// <summary>
        /// Whether <paramref name="resource"/> has already been declared resident on
        /// this encoder for the same usage and stages, which lasts until the encoder
        /// ends and so does not need repeating. The encoder retains what it has been
        /// given, so a live declaration's pointer cannot be recycled underneath this.
        /// </summary>
        public bool IsResident(MTLRenderCommandEncoder encoder, IntPtr resource, MTLResourceUsage usage, MTLRenderStages stages)
        {
            Retarget(encoder);

            // A single pass can hold an unbounded number of draws, so stop growing the
            // set rather than trade a call rate for a memory leak. Declaring a
            // resource again is wasteful, never wrong.
            if (_resident.Count >= MaxResidentTracked)
            {
                return false;
            }

            bool already = !_resident.Add((resource, usage, stages));

            return _level >= LevelResidency && already;
        }

        private void Retarget(MTLRenderCommandEncoder encoder)
        {
            // The pointer alone is not an identity: a released encoder's address is
            // routinely reused by the next one, and treating that as the same encoder
            // keeps every field marked applied, so nothing is ever set on the new
            // encoder - the first draw then faults in the driver on a null pipeline.
            long generation = CommandBufferEncoder.RenderEncoderGeneration;

            if (_encoder == encoder.NativePtr && _generation == generation)
            {
                return;
            }

            _encoder = encoder.NativePtr;
            _generation = generation;
            _known = default;
            _vertexBound = 0;
            _fragmentBound = 0;
            _resident.Clear();
        }
    }

    [SupportedOSPlatform("macos")]
    struct EncoderStateManager : IDisposable
    {
        private const int ArrayGrowthSize = 16;

        private readonly MTLDevice _device;
        private readonly Pipeline _pipeline;
        private readonly BufferManager _bufferManager;

        private readonly DepthStencilCache _depthStencilCache;
        private readonly MTLDepthStencilState _defaultState;
        private readonly AppliedRenderState _applied = new();

        private readonly EncoderState _mainState = new();
        private EncoderState _currentState;

        internal readonly EncoderState CurrentEncoderState => _currentState;

        /// <summary>
        /// True when the render encoder has a valid, non-null pipeline state set.
        /// Draw calls must check this and skip when false to avoid a SIGSEGV in
        /// the Metal driver (async shader compilation can leave the PSO null).
        /// </summary>
        public bool HasValidRenderPipeline => _applied.PipelineValid;

        public readonly IndexBufferState IndexBuffer => _currentState.IndexBuffer;
        public readonly PrimitiveTopology Topology => _currentState.Topology;
        public readonly Texture[] RenderTargets => _currentState.RenderTargets;
        public readonly Program RenderProgram => _currentState.RenderProgram;
        public readonly Program ComputeProgram => _currentState.ComputeProgram;

        /// <summary>
        /// Diagnostic companion to the draw trace: for every per-instance vertex
        /// buffer binding, log its layout and the CPU-visible bytes of the record
        /// the given first instance will fetch, plus which attributes read it.
        /// </summary>
        public readonly void TraceDumpInstanceBuffers(int firstInstance)
        {
            for (int i = 0; i < _currentState.VertexBuffers.Length; i++)
            {
                VertexBufferState vertexBuffer = _currentState.VertexBuffers[i];

                if (vertexBuffer.Divisor <= 0 || vertexBuffer.Stride <= 0)
                {
                    continue;
                }

                int record = firstInstance / vertexBuffer.Divisor;
                string recordText = vertexBuffer.DescribeRecordForTrace(_bufferManager, record);

                if (recordText == null)
                {
                    continue;
                }

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"trace vb[{i}] stride={vertexBuffer.Stride} div={vertexBuffer.Divisor} rec[{record}]={recordText}");
            }

            for (int i = 0; i < _currentState.VertexAttribs.Length; i++)
            {
                VertexAttribDescriptor attrib = _currentState.VertexAttribs[i];

                if (attrib.IsZero || attrib.BufferIndex >= _currentState.VertexBuffers.Length)
                {
                    continue;
                }

                if (_currentState.VertexBuffers[attrib.BufferIndex].Divisor > 0)
                {
                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        $"trace attr{i} vb={attrib.BufferIndex} off={attrib.Offset} fmt={attrib.Format}");
                }
            }
        }

        /// <summary>
        /// Diagnostic companion to the draw trace: dump the CPU-visible contents of
        /// every bound uniform (constant) buffer as vec4 floats. Comparing the same
        /// draw across the three traced frames shows whether an animation input (e.g.
        /// the gloom flow phase in fp_c4[9].z) actually advances per frame on this
        /// backend, or is frozen. All buffers are storageModeShared so Contents is
        /// the live value the shader will read.
        /// </summary>
        /// <summary>
        /// Diagnostic companion to the draw trace: for every bound texture, log its
        /// target (2D/3D/Cube), format, dimensions and mip levels. The gloom material
        /// samples four 3D volumetric-noise textures that drive its discard threshold;
        /// a wrong target or format here would make the noise wrong and over-discard
        /// the gloom (sparse coverage on Metal vs full field on Vulkan).
        /// </summary>
        public readonly void TraceDumpTextures(string tag)
        {
            for (int i = 0; i < _currentState.TextureRefs.Length; i++)
            {
                ref TextureRef texRef = ref _currentState.TextureRefs[i];

                if (texRef.Storage == null)
                {
                    continue;
                }

                TextureCreateInfo info = texRef.Storage.Info;

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"trace tex[{i}] {tag} stage={texRef.Stage} target={info.Target} fmt={info.Format} {info.Width}x{info.Height}x{info.Depth} levels={info.Levels} imgFmt={texRef.ImageFormat}");
            }
        }

        public readonly unsafe void TraceDumpUniformBuffers(string tag)
        {
            for (int i = 0; i < _currentState.UniformBufferRefs.Length; i++)
            {
                ref BufferRef bufferRef = ref _currentState.UniformBufferRefs[i];

                if (bufferRef.Buffer == null)
                {
                    continue;
                }

                MTLBuffer mtlBuffer = bufferRef.Buffer.GetUnsafe().Value;

                if (mtlBuffer.NativePtr == IntPtr.Zero || mtlBuffer.Contents == IntPtr.Zero)
                {
                    continue;
                }

                int offset = bufferRef.Range?.Offset ?? 0;
                int size = bufferRef.Range?.Size ?? 256;

                int vec4Count = Math.Min(16, size / 16);

                if (vec4Count <= 0)
                {
                    continue;
                }

                float* data = (float*)((byte*)mtlBuffer.Contents + offset);

                System.Text.StringBuilder builder = new();
                builder.Append($"trace cbuf[{i}] {tag} off={offset} size={size}:");

                for (int v = 0; v < vec4Count; v++)
                {
                    builder.Append($" [{v}]({data[v * 4].ToString("G6")},{data[v * 4 + 1].ToString("G6")},{data[v * 4 + 2].ToString("G6")},{data[v * 4 + 3].ToString("G6")})");
                }

                Logger.Warning?.PrintMsg(LogClass.Gpu, builder.ToString());
            }
        }
        public readonly Texture DepthStencil => _currentState.DepthStencil;
        public readonly ComputeSize ComputeLocalSize => _currentState.ComputeProgram.ComputeLocalSize;

        // RGBA32F is the biggest format
        private const int ZeroBufferSize = 4 * 4;
        private readonly BufferHandle _zeroBuffer;

        public unsafe EncoderStateManager(MTLDevice device, BufferManager bufferManager, Pipeline pipeline)
        {
            _device = device;
            _pipeline = pipeline;
            _bufferManager = bufferManager;

            _depthStencilCache = new(device);
            _currentState = _mainState;

            _defaultState = _depthStencilCache.GetOrCreate(_currentState.DepthStencilUid);

            // Zero buffer
            byte[] zeros = new byte[ZeroBufferSize];
            fixed (byte* ptr = zeros)
            {
                _zeroBuffer = _bufferManager.Create((IntPtr)ptr, ZeroBufferSize);
            }
        }

        public readonly void Dispose()
        {
            _mainState.RenderEncoderBindings.DisposeTemporaryBuffers();
            _mainState.ComputeEncoderBindings.DisposeTemporaryBuffers();

            if (_currentState != _mainState)
            {
                _currentState.RenderEncoderBindings.DisposeTemporaryBuffers();
                _currentState.ComputeEncoderBindings.DisposeTemporaryBuffers();
            }

            _depthStencilCache.Dispose();
        }

        public readonly void DisposeRenderTemporaryBuffers()
        {
            _currentState.RenderEncoderBindings.DisposeTemporaryBuffers();
        }

        public readonly void DisposeComputeTemporaryBuffers()
        {
            _currentState.ComputeEncoderBindings.DisposeTemporaryBuffers();
        }

        private readonly void SignalDirty(DirtyFlags flags)
        {
            _currentState.Dirty |= flags;
        }

        /// <summary>
        /// A bound buffer range changed identity - it gained or lost a mirror - so the
        /// argument buffers that name it have to be rebuilt.
        /// </summary>
        public readonly void SignalBufferRebind()
        {
            SignalDirty(DirtyFlags.Uniforms | DirtyFlags.Storages);
        }

        public readonly void SignalRenderDirty()
        {
            SignalDirty(DirtyFlags.RenderAll);
        }

        public readonly void SignalComputeDirty()
        {
            SignalDirty(DirtyFlags.ComputeAll);
        }

        public EncoderState SwapState(EncoderState state, DirtyFlags flags = DirtyFlags.All)
        {
            _currentState = state ?? _mainState;

            SignalDirty(flags);

            return _mainState;
        }

        public PredrawState SavePredrawState()
        {
            return new PredrawState
            {
                CullMode = _currentState.CullMode,
                DepthStencilUid = _currentState.DepthStencilUid,
                Topology = _currentState.Topology,
                Viewports = _currentState.Viewports.ToArray(),
            };
        }

        public readonly void RestorePredrawState(PredrawState state)
        {
            _currentState.CullMode = state.CullMode;
            _currentState.DepthStencilUid = state.DepthStencilUid;
            _currentState.Topology = state.Topology;
            _currentState.Viewports = state.Viewports;

            SignalDirty(DirtyFlags.CullMode | DirtyFlags.DepthStencil | DirtyFlags.Viewports);
        }

        public readonly void SetClearLoadAction(bool clear)
        {
            _currentState.ClearLoadAction = clear;
        }

        public readonly void DirtyTextures()
        {
            SignalDirty(DirtyFlags.Textures);
        }

        public readonly void DirtyImages()
        {
            SignalDirty(DirtyFlags.Images);
        }

        public readonly MTLRenderCommandEncoder CreateRenderCommandEncoder()
        {
            // Initialise Pass & State
            using MTLRenderPassDescriptor renderPassDescriptor = new();

            FeedbackProbe.BeginPass();

            _passStoreUnknown = _elideEmptyStore;
            _passColorMask = 0;
            _passHasDepth = false;
            _passHasStencil = false;
            _passCleared = _currentState.ClearLoadAction;

            Texture sceneCheck = _currentState.RenderTargets[0] as Texture;
            _passSceneClass = sceneCheck != null &&
                sceneCheck.Width >= 1500 && sceneCheck.Width <= 1700 &&
                sceneCheck.MtlFormat == MTLPixelFormat.RG11B10Float;

            NotePassSize(0, 0);

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                if (_currentState.RenderTargets[i] is Texture tex)
                {
                    MTLRenderPassColorAttachmentDescriptor passAttachment = renderPassDescriptor.ColorAttachments.Object((ulong)i);
                    tex.PopulateRenderPassAttachment(passAttachment, _pipeline.Cbs);
                    passAttachment.LoadAction = _currentState.ClearLoadAction ? MTLLoadAction.Clear : MTLLoadAction.Load;
                    passAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                    _passColorMask |= 1ul << i;

                    // The attachments this pass really carries. Comparing against
                    // _currentState.RenderTargets instead counted targets bound for an
                    // earlier pass, which is what buried the feedback signal twice.
                    FeedbackProbe.NoteAttachment(i, tex);
                    UploadCorrelator.NoteAttachment(tex);
                    NoteAttachmentWritten(tex.CanonicalPtr);
                    NotePassSize((ulong)tex.Width, (ulong)tex.Height);
                }
            }

            if (_passWidth == 0 && _currentState.DepthStencil != null)
            {
                NotePassSize((ulong)_currentState.DepthStencil.Width, (ulong)_currentState.DepthStencil.Height);
            }

            MTLRenderPassDepthAttachmentDescriptor depthAttachment = renderPassDescriptor.DepthAttachment;
            MTLRenderPassStencilAttachmentDescriptor stencilAttachment = renderPassDescriptor.StencilAttachment;

            if (_currentState.DepthStencil != null)
            {
                switch (_currentState.DepthStencil.GetHandle().PixelFormat)
                {
                    // Depth Only Attachment
                    case MTLPixelFormat.Depth16Unorm:
                    case MTLPixelFormat.Depth32Float:
                        // Must be the identity handle, not the swizzled view: a Metal
                        // texture view created with a swizzle does not carry
                        // MTLTextureUsageRenderTarget, and binding one as an attachment
                        // is undefined behaviour - depth writes are silently dropped.
                        // Colour attachments already use the identity handle.
                        depthAttachment.Texture = _currentState.DepthStencil.GetIdentityHandle(_pipeline.Cbs);
                        depthAttachment.LoadAction = MTLLoadAction.Load;
                        depthAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                        _passHasDepth = true;
                        break;

                    // Stencil Only Attachment
                    case MTLPixelFormat.Stencil8:
                        stencilAttachment.Texture = _currentState.DepthStencil.GetIdentityHandle(_pipeline.Cbs);
                        stencilAttachment.LoadAction = MTLLoadAction.Load;
                        stencilAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                        _passHasStencil = true;
                        break;

                    // Combined Attachment
                    case MTLPixelFormat.Depth24UnormStencil8:
                    case MTLPixelFormat.Depth32FloatStencil8:
                        depthAttachment.Texture = _currentState.DepthStencil.GetIdentityHandle(_pipeline.Cbs);
                        depthAttachment.LoadAction = MTLLoadAction.Load;
                        depthAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                        _passHasDepth = true;

                        stencilAttachment.Texture = _currentState.DepthStencil.GetIdentityHandle(_pipeline.Cbs);
                        stencilAttachment.LoadAction = MTLLoadAction.Load;
                        stencilAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                        _passHasStencil = true;
                        break;
                    default:
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"Unsupported Depth/Stencil Format: {_currentState.DepthStencil.GetHandle().PixelFormat}!");
                        break;
                }
            }

            // A render pass with no attachments at all is invalid: Metal's validation
            // layer reports "No output textures defined for the render pass" and the
            // encoder's output is undefined. Ryujinx creates these whenever the guest
            // has neither a colour target nor a depth/stencil target bound. Configure
            // targetless rasterization instead, which is the supported way to encode
            // draws that only have side effects.
            bool hasAttachment = _currentState.DepthStencil != null;

            if (!hasAttachment)
            {
                for (int i = 0; i < Constants.MaxColorAttachments; i++)
                {
                    if (_currentState.RenderTargets[i] != null)
                    {
                        hasAttachment = true;
                        break;
                    }
                }
            }

            if (!hasAttachment)
            {
                // The wrapper is a struct over the same native object, so a local copy
                // still configures the descriptor being built.
                MTLRenderPassDescriptor targetless = renderPassDescriptor;

                targetless.RenderTargetWidth = (ulong)Math.Max(1, _currentState.Viewports.Length > 0 ? (int)_currentState.Viewports[0].width : 1);
                targetless.RenderTargetHeight = (ulong)Math.Max(1, _currentState.Viewports.Length > 0 ? (int)_currentState.Viewports[0].height : 1);
                targetless.DefaultRasterSampleCount = 1;
            }

            // Record what this pass writes, so the present-time sweep can walk the chain
            // from scene colour to present source. Bookkeeping only - no Metal call, no
            // pass split, so it cannot move the race it is measuring.
            if (FrameProbe.Enabled)
            {
                FrameProbe.NotePass(_currentState.RenderTargets, _currentState.DepthStencil);
            }

            OpRing.NotePass(
                _currentState.RenderTargets[0]?.GetHandle().NativePtr ?? IntPtr.Zero,
                _currentState.DepthStencil?.GetHandle().NativePtr ?? IntPtr.Zero);

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.BeginPassAll(_currentState.RenderTargets, _currentState.ClearLoadAction);

                foreach (Texture rt in _currentState.RenderTargets)
                {
                    HdrPassProbe.NoteIdentity("target", rt);
                }
            }

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.NoteWriterProgram(_currentState.RenderTargets[0], _currentState.RenderProgram?.DebugLabel);
            }

            bool countSamples = _pipeline.SupportsSamplesPassed;
            ulong visibilityOffset = 0;

            // A pass descriptor carries one visibility buffer, so the coverage probe and
            // the guest's occlusion queries cannot both have it. The probe takes only the
            // passes into the watched composite, and only when it is switched on.
            bool measureCoverage = CoverageProbe.Enabled && CoverageProbe.TryTakePass(
                _pipeline.FrameSlot,
                _currentState.RenderTargets.Length > 0 ? _currentState.RenderTargets[0] : null,
                _currentState.Scissors.Length > 0 ? _currentState.Scissors[0] : default,
                _currentState.Viewports.Length > 0 ? _currentState.Viewports[0] : default,
                _currentState.DepthStencilUid.DepthCompareFunction,
                renderPassDescriptor,
                out visibilityOffset);

            if (!measureCoverage && countSamples)
            {
                visibilityOffset = _pipeline.PrepareCounterRenderPass(renderPassDescriptor);
            }

            // Initialise Encoder
            MTLRenderCommandEncoder renderCommandEncoder = _pipeline.CommandBuffer.RenderCommandEncoder(renderPassDescriptor);

            if (countSamples || measureCoverage)
            {
                renderCommandEncoder.SetVisibilityResultMode(MTLVisibilityResultMode.Counting, visibilityOffset);
            }

            return renderCommandEncoder;
        }

        /// <summary>
        /// Resolves the deferred store actions when the pass ends. Zero draws and nothing
        /// cleared means nothing in tile memory can differ from what the load brought in,
        /// so DontCare skips the write-back entirely - the pass stops touching memory.
        /// </summary>
        public readonly void FixupStoreActions(MTLRenderCommandEncoder encoder, ulong drawsInPass)
        {
            if (!_passStoreUnknown)
            {
                return;
            }

            MTLStoreAction action = drawsInPass == 0 && !_passCleared && _passSceneClass
                ? MTLStoreAction.DontCare
                : MTLStoreAction.Store;

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                if ((_passColorMask & (1ul << i)) != 0)
                {
                    encoder.SetColorStoreAction(action, (ulong)i);
                }
            }

            if (_passHasDepth)
            {
                encoder.SetDepthStoreAction(action);
            }

            if (_passHasStencil)
            {
                encoder.SetStencilStoreAction(action);
            }

            _passStoreUnknown = false;
        }

        public readonly MTLComputeCommandEncoder CreateComputeCommandEncoder()
        {
            using MTLComputePassDescriptor descriptor = new();
            MTLComputeCommandEncoder computeCommandEncoder = _pipeline.CommandBuffer.ComputeCommandEncoder(descriptor);

            return computeCommandEncoder;
        }

        // Diagnostic probe: with RYUJINX_METAL_FULL_REBIND=1 every draw re-writes the
        // argument buffers and residency lists from the CURRENT texture/buffer
        // handles, ignoring dirty tracking. Costs performance; if it eliminates the
        // stale-content symptoms (vanishing baked foliage, frozen gloom), the dirty
        // tracking is missing handle-invalidation signals.
        private static readonly bool _forceFullRebind =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FULL_REBIND") == "1";

        public readonly void RenderResourcesPrepass()
        {
            _currentState.RenderEncoderBindings.Clear();

            if (_forceFullRebind)
            {
                _currentState.Dirty |= DirtyFlags.RenderPipeline | DirtyFlags.Uniforms |
                    DirtyFlags.Storages | DirtyFlags.Textures | DirtyFlags.Images;
            }

            if ((_currentState.Dirty & DirtyFlags.RenderPipeline) != 0)
            {
                SetVertexBuffers(_currentState.VertexBuffers, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Uniforms) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.ConstantBuffersSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Storages) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.StorageBuffersSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Textures) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.TexturesSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Images) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.ImagesSetIndex, ref _currentState.RenderEncoderBindings);
            }
        }

        public readonly void ComputeResourcesPrepass()
        {
            _currentState.ComputeEncoderBindings.Clear();

            if (_forceFullRebind)
            {
                _currentState.Dirty |= DirtyFlags.Uniforms | DirtyFlags.Storages |
                    DirtyFlags.Textures | DirtyFlags.Images;
            }

            if ((_currentState.Dirty & DirtyFlags.Uniforms) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.ConstantBuffersSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Storages) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.StorageBuffersSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Textures) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.TexturesSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Images) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.ImagesSetIndex, ref _currentState.ComputeEncoderBindings);
            }
        }

        public void RebindRenderState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if ((_currentState.Dirty & DirtyFlags.RenderPipeline) != 0)
            {
                SetRenderPipelineState(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthStencil) != 0)
            {
                SetDepthStencilState(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthClamp) != 0)
            {
                SetDepthClamp(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthBias) != 0)
            {
                SetDepthBias(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.CullMode) != 0)
            {
                SetCullMode(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.FrontFace) != 0)
            {
                SetFrontFace(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.StencilRef) != 0)
            {
                SetStencilRefValue(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.Viewports) != 0)
            {
                SetViewports(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.Scissors) != 0)
            {
                SetScissors(renderCommandEncoder);
            }

            UseRenderResources(renderCommandEncoder, ref _currentState.RenderEncoderBindings);

            foreach (BufferResource buffer in _currentState.RenderEncoderBindings.VertexBuffers)
            {
                if (!_applied.IsBound(renderCommandEncoder, buffer, fragment: false))
                {
                    renderCommandEncoder.SetVertexBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
                }
            }

            foreach (BufferResource buffer in _currentState.RenderEncoderBindings.FragmentBuffers)
            {
                if (!_applied.IsBound(renderCommandEncoder, buffer, fragment: true))
                {
                    renderCommandEncoder.SetFragmentBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
                }
            }

            _currentState.Dirty &= ~DirtyFlags.RenderAll;

            // A pipeline that failed to build has to stay dirty. Clearing the flag would
            // stop the state cache from ever rebuilding it, and since the draw guard skips
            // on an invalid pipeline, nothing would ever run SetRenderPipelineState again -
            // one failed build would blank every later frame. Retrying each draw instead
            // lets the draw recover as soon as async shader compilation finishes.
            if (!_applied.PipelineValid)
            {
                _currentState.Dirty |= DirtyFlags.RenderPipeline;
            }
        }

        public void RebindComputeState(MTLComputeCommandEncoder computeCommandEncoder)
        {
            if ((_currentState.Dirty & DirtyFlags.ComputePipeline) != 0)
            {
                SetComputePipelineState(computeCommandEncoder);
            }

            UseComputeResources(computeCommandEncoder, ref _currentState.ComputeEncoderBindings);

            foreach (BufferResource buffer in _currentState.ComputeEncoderBindings.Buffers)
            {
                computeCommandEncoder.SetBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
            }

            _currentState.Dirty &= ~DirtyFlags.ComputeAll;
        }

        private readonly void UseRenderResources(MTLRenderCommandEncoder renderCommandEncoder, ref RenderEncoderBindings bindings)
        {
            if (bindings.Resources.Count == 0)
            {
                return;
            }

            MTLResource[] resources = bindings.GetResourceScratch(bindings.Resources.Count);

            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read, MTLRenderStages.RenderStageVertex);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read, MTLRenderStages.RenderStageFragment);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read, MTLRenderStages.RenderStageVertex | MTLRenderStages.RenderStageFragment);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read | MTLResourceUsage.Write, MTLRenderStages.RenderStageVertex);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read | MTLResourceUsage.Write, MTLRenderStages.RenderStageFragment);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read | MTLResourceUsage.Write, MTLRenderStages.RenderStageVertex | MTLRenderStages.RenderStageFragment);
        }

        private readonly void UseRenderResources(
            MTLRenderCommandEncoder renderCommandEncoder,
            List<Resource> bindings,
            MTLResource[] resources,
            MTLResourceUsage usage,
            MTLRenderStages stages)
        {
            int count = 0;

            foreach (Resource binding in bindings)
            {
                // Residency lasts for the life of the encoder, so a resource already
                // declared under this usage and stages does not need declaring again.
                if (binding.ResourceUsage == usage && binding.Stages == stages &&
                    !_applied.IsResident(renderCommandEncoder, binding.MtlResource.NativePtr, usage, stages))
                {
                    resources[count++] = binding.MtlResource;
                }
            }

            if (count != 0)
            {
                renderCommandEncoder.UseResourcesCompat(resources, (ulong)count, usage, stages);
            }
        }

        private static void UseComputeResources(MTLComputeCommandEncoder computeCommandEncoder, ref ComputeEncoderBindings bindings)
        {
            if (bindings.Resources.Count == 0)
            {
                return;
            }

            MTLResource[] resources = bindings.GetResourceScratch(bindings.Resources.Count);

            UseComputeResources(computeCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read);
            UseComputeResources(computeCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read | MTLResourceUsage.Write);
        }

        private static void UseComputeResources(
            MTLComputeCommandEncoder computeCommandEncoder,
            List<Resource> bindings,
            MTLResource[] resources,
            MTLResourceUsage usage)
        {
            int count = 0;

            foreach (Resource binding in bindings)
            {
                if (binding.ResourceUsage == usage)
                {
                    resources[count++] = binding.MtlResource;
                }
            }

            if (count != 0)
            {
                computeCommandEncoder.UseResourcesCompat(resources, (ulong)count, usage);
            }
        }

        private readonly void SetRenderPipelineState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            MTLRenderPipelineState pipelineState = _currentState.Pipeline.CreateRenderPipeline(_device, _currentState.RenderProgram);

            // Compilation failed (async shader compile not finished, or a genuinely
            // bad pipeline). Leave whatever is on the encoder alone and report the
            // state as unusable, so the caller skips the draw instead of letting the
            // Metal driver dereference a null pipeline inside drawPrimitives.
            _applied.PipelineValid = pipelineState.NativePtr != IntPtr.Zero;

            if (!_applied.PipelineValid)
            {
                return;
            }

            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.Pipeline) ||
                _applied.PipelineState != pipelineState.NativePtr)
            {
                _applied.PipelineState = pipelineState.NativePtr;

                renderCommandEncoder.SetRenderPipelineState(pipelineState);
            }

            // The blend colour is not part of the pipeline object, but every dirty
            // flag that rebuilds the pipeline used to resend it too - by far the most
            // repeated redundant encoder call in a frame.
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.BlendColor) ||
                _applied.BlendColor != _currentState.BlendColor)
            {
                _applied.BlendColor = _currentState.BlendColor;

                renderCommandEncoder.SetBlendColor(
                    _currentState.BlendColor.Red,
                    _currentState.BlendColor.Green,
                    _currentState.BlendColor.Blue,
                    _currentState.BlendColor.Alpha);
            }
        }

        private readonly void SetComputePipelineState(MTLComputeCommandEncoder computeCommandEncoder)
        {
            if (_currentState.ComputeProgram == null)
            {
                return;
            }

            MTLComputePipelineState pipelineState = PipelineState.CreateComputePipeline(_device, _currentState.ComputeProgram);

            computeCommandEncoder.SetComputePipelineState(pipelineState);
        }

        public readonly void UpdateIndexBuffer(BufferRange buffer, IndexType type)
        {
            if (buffer.Handle != BufferHandle.Null)
            {
                _currentState.IndexBuffer = new IndexBufferState(buffer.Handle, buffer.Offset, buffer.Size, type);
            }
            else
            {
                _currentState.IndexBuffer = IndexBufferState.Null;
            }
        }

        public readonly void UpdatePrimitiveTopology(PrimitiveTopology topology)
        {
            _currentState.Topology = topology;
        }

        public readonly void UpdateProgram(IProgram program)
        {
            Program prg = (Program)program;

            if (prg.VertexFunction == IntPtr.Zero && prg.ComputeFunction == IntPtr.Zero)
            {
                if (prg.FragmentFunction == IntPtr.Zero)
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu, "No compute function");
                }
                else
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu, "No vertex function");
                }
                return;
            }

            if (prg.VertexFunction != IntPtr.Zero)
            {
                _currentState.RenderProgram = prg;
                _currentState.DrawRingCb1Address = 0;
                _currentState.DrawRingCb3Address = 0;
                _currentState.DrawRingTex8ResourceId = 0;
                _currentState.DrawRingTexAResourceId = 0;

                SignalDirty(DirtyFlags.RenderPipeline | DirtyFlags.ArgBuffers);
            }
            else if (prg.ComputeFunction != IntPtr.Zero)
            {
                _currentState.ComputeProgram = prg;

                SignalDirty(DirtyFlags.ComputePipeline | DirtyFlags.ArgBuffers);
            }
        }

        public readonly void UpdateRasterizerDiscard(bool discard)
        {
            _currentState.Pipeline.RasterizerDiscardEnable = discard;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateRenderTargets(Span<ITexture> colors, ITexture depthStencil)
        {
            _currentState.FramebufferUsingColorWriteMask = false;
            UpdateRenderTargetsInternal(colors, depthStencil);
        }

        public readonly void UpdateRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            ref Array8<ColorBlendStateUid> blendState = ref _currentState.Pipeline.Internal.ColorBlendState;

            for (int i = 0; i < componentMask.Length; i++)
            {
                bool red = (componentMask[i] & (0x1 << 0)) != 0;
                bool green = (componentMask[i] & (0x1 << 1)) != 0;
                bool blue = (componentMask[i] & (0x1 << 2)) != 0;
                bool alpha = (componentMask[i] & (0x1 << 3)) != 0;

                MTLColorWriteMask mask = MTLColorWriteMask.None;

                mask |= red ? MTLColorWriteMask.Red : 0;
                mask |= green ? MTLColorWriteMask.Green : 0;
                mask |= blue ? MTLColorWriteMask.Blue : 0;
                mask |= alpha ? MTLColorWriteMask.Alpha : 0;

                ref ColorBlendStateUid mtlBlend = ref blendState[i];

                // When color write mask is 0, remove all blend state to help the pipeline cache.
                // Restore it when the mask becomes non-zero.
                if (mtlBlend.WriteMask != mask)
                {
                    if (mask == 0)
                    {
                        _currentState.StoredBlend[i] = mtlBlend;

                        mtlBlend.Swap(new ColorBlendStateUid());
                    }
                    else if (mtlBlend.WriteMask == 0)
                    {
                        mtlBlend.Swap(_currentState.StoredBlend[i]);
                    }
                }

                blendState[i].WriteMask = mask;
            }

            if (_currentState.FramebufferUsingColorWriteMask)
            {
                UpdateRenderTargetsInternal(_currentState.PreMaskRenderTargets, _currentState.PreMaskDepthStencil);
            }
            else
            {
                // The write mask lives in the render pipeline descriptor, not the render
                // pass descriptor, and it is part of PipelineUid, so a new pipeline object
                // is all a change needs. Ending the pass was only a roundabout way of
                // forcing that rebuild, and it cost a full attachment store and reload
                // every time the guest changed a mask - 43 render passes a frame.
                if (_endPassOnColorMask && _pipeline.CurrentEncoderType == EncoderType.Render)
                {
                    _pipeline.EndCurrentPass(PassEndReason.ColorMask);
                }
                else
                {
                    SignalDirty(DirtyFlags.RenderPipeline);
                }
            }
        }

        // A/B switch for the above: RYUJINX_METAL_END_PASS_ON_COLOR_MASK=1 restores the
        // pass split.
        private static readonly bool _endPassOnColorMask =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_END_PASS_ON_COLOR_MASK") == "1";

        private readonly void UpdateRenderTargetsInternal(Span<ITexture> colors, ITexture depthStencil)
        {
            // TBDR GPUs don't work properly if the same attachment is bound to multiple targets,
            // due to each attachment being a copy of the real attachment, rather than a direct write.
            //
            // Just try to remove duplicate attachments.
            // Save a copy of the array to rebind when mask changes.

            // Look for textures that are masked out.

            ref PipelineState pipeline = ref _currentState.Pipeline;
            ref Array8<ColorBlendStateUid> blendState = ref pipeline.Internal.ColorBlendState;

            pipeline.ColorBlendAttachmentStateCount = (uint)colors.Length;

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] == null)
                {
                    continue;
                }

                MTLColorWriteMask mtlMask = blendState[i].WriteMask;

                for (int j = 0; j < i; j++)
                {
                    // Check each binding for a duplicate binding before it.
                    //
                    // By storage, not by reference: two views of one storage are different
                    // ITexture objects, so reference equality misses exactly the case this
                    // dedup exists for - and with both slots left attached, the fragment
                    // shader stores a defined value through one and an undefined value
                    // through the other, into the same memory. That is the profile of the
                    // TOTK white flash measured on 2026-08-09: intermittent, invisible to
                    // every identity-keyed probe, no copy, no clear, no compute, and
                    // landing exactly at the zero-draw mask passes at the end of the frame.
                    bool sameStorage = colors[i] == colors[j] ||
                        (!_dedupByReferenceOnly &&
                            colors[i] is Texture ti && colors[j] is Texture tj &&
                            ti.CanonicalPtr != IntPtr.Zero && ti.CanonicalPtr == tj.CanonicalPtr);

                    if (sameStorage)
                    {
                        // Prefer the binding with no write mask.

                        MTLColorWriteMask mtlMask2 = blendState[j].WriteMask;

                        if (mtlMask == 0)
                        {
                            colors[i] = null;
                            MaskOut(colors, depthStencil);
                        }
                        else if (mtlMask2 == 0)
                        {
                            colors[j] = null;
                            MaskOut(colors, depthStencil);
                        }
                    }
                }
            }

            _currentState.RenderTargets = new Texture[Constants.MaxColorAttachments];

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is not Texture tex)
                {
                    blendState[i].PixelFormat = MTLPixelFormat.Invalid;

                    continue;
                }

                blendState[i].PixelFormat = tex.GetHandle().PixelFormat; // TODO: cache this
                _currentState.RenderTargets[i] = tex;
            }

            if (depthStencil is Texture depthTexture)
            {
                pipeline.DepthStencilFormat = depthTexture.GetHandle().PixelFormat; // TODO: cache this
                _currentState.DepthStencil = depthTexture;
            }
            else if (depthStencil == null)
            {
                pipeline.DepthStencilFormat = MTLPixelFormat.Invalid;
                _currentState.DepthStencil = null;
            }

            // Measured: the attachment set really does change on all 116 of these a
            // frame, so there is no redundant case here to skip.
            if (_pipeline.CurrentEncoderType == EncoderType.Render)
            {
                _pipeline.EndCurrentPass(PassEndReason.RenderTargets);
            }
        }

        private readonly void MaskOut(Span<ITexture> colors, ITexture depthStencil)
        {
            if (!_currentState.FramebufferUsingColorWriteMask)
            {
                _currentState.PreMaskRenderTargets = colors.ToArray();
                _currentState.PreMaskDepthStencil = depthStencil;
            }

            // If true, then the framebuffer must be recreated when the mask changes.
            _currentState.FramebufferUsingColorWriteMask = true;
        }

        public readonly void UpdateVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            vertexAttribs.CopyTo(_currentState.VertexAttribs);

            // Update the buffers on the pipeline
            UpdatePipelineVertexState(_currentState.VertexBuffers, _currentState.VertexAttribs);

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateBlendDescriptors(int index, BlendDescriptor blend)
        {
            ref ColorBlendStateUid blendState = ref _currentState.Pipeline.Internal.ColorBlendState[index];

            blendState.Enable = blend.Enable;
            blendState.AlphaBlendOperation = blend.AlphaOp.Convert();
            blendState.RgbBlendOperation = blend.ColorOp.Convert();
            blendState.SourceAlphaBlendFactor = blend.AlphaSrcFactor.Convert();
            blendState.DestinationAlphaBlendFactor = blend.AlphaDstFactor.Convert();
            blendState.SourceRGBBlendFactor = blend.ColorSrcFactor.Convert();
            blendState.DestinationRGBBlendFactor = blend.ColorDstFactor.Convert();

            if (blendState.WriteMask == 0)
            {
                _currentState.StoredBlend[index] = blendState;

                blendState.Swap(new ColorBlendStateUid());
            }

            _currentState.BlendColor = blend.BlendConstant;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public void UpdateStencilState(StencilTestDescriptor stencilTest)
        {
            ref DepthStencilUid uid = ref _currentState.DepthStencilUid;

            uid.FrontFace = new StencilUid
            {
                StencilFailureOperation = stencilTest.FrontSFail.Convert(),
                DepthFailureOperation = stencilTest.FrontDpFail.Convert(),
                DepthStencilPassOperation = stencilTest.FrontDpPass.Convert(),
                StencilCompareFunction = stencilTest.FrontFunc.Convert(),
                ReadMask = (uint)stencilTest.FrontFuncMask,
                WriteMask = (uint)stencilTest.FrontMask
            };

            uid.BackFace = new StencilUid
            {
                StencilFailureOperation = stencilTest.BackSFail.Convert(),
                DepthFailureOperation = stencilTest.BackDpFail.Convert(),
                DepthStencilPassOperation = stencilTest.BackDpPass.Convert(),
                StencilCompareFunction = stencilTest.BackFunc.Convert(),
                ReadMask = (uint)stencilTest.BackFuncMask,
                WriteMask = (uint)stencilTest.BackMask
            };

            uid.StencilTestEnabled = stencilTest.TestEnable;

            UpdateStencilRefValue(stencilTest.FrontFuncRef, stencilTest.BackFuncRef);

            SignalDirty(DirtyFlags.DepthStencil);
        }

        /// <summary>
        /// Diagnostic: RYUJINX_METAL_RELAX_DEPTH_EQUAL replaces the Equal depth
        /// comparison with the named one (Always / LessEqual). Geometry drawn with
        /// Equal is shaded in a second pass that must match a depth prepass exactly;
        /// relaxing the comparison tells "the prepass never wrote this depth" apart
        /// from "the geometry never reached the rasteriser".
        /// </summary>
        private static readonly MTLCompareFunction? _relaxDepthEqual =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_RELAX_DEPTH_EQUAL") switch
            {
                "always" or "1" => MTLCompareFunction.Always,
                "lessequal" or "lequal" => MTLCompareFunction.LessEqual,
                _ => null,
            };

        public readonly void UpdateDepthState(DepthTestDescriptor depthTest)
        {
            ref DepthStencilUid uid = ref _currentState.DepthStencilUid;

            MTLCompareFunction compare = depthTest.TestEnable ? depthTest.Func.Convert() : MTLCompareFunction.Always;

            if (_relaxDepthEqual.HasValue && compare == MTLCompareFunction.Equal)
            {
                compare = _relaxDepthEqual.Value;
            }

            uid.DepthCompareFunction = compare;
            uid.DepthWriteEnabled = depthTest.TestEnable && depthTest.WriteEnable;

            SignalDirty(DirtyFlags.DepthStencil);
        }

        public readonly void UpdateDepthClamp(bool clamp)
        {
            _currentState.DepthClipMode = clamp ? MTLDepthClipMode.Clamp : MTLDepthClipMode.Clip;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetDepthClamp(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.DepthClamp);
        }

        public readonly void UpdateDepthBias(float depthBias, float slopeScale, float clamp)
        {
            _currentState.DepthBias = depthBias;
            _currentState.SlopeScale = slopeScale;
            _currentState.Clamp = clamp;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetDepthBias(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.DepthBias);
        }

        public readonly void UpdateLogicOpState(bool enable, LogicalOp op)
        {
            _currentState.Pipeline.LogicOpEnable = enable;
            _currentState.Pipeline.LogicOp = op.Convert();

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateMultisampleState(MultisampleDescriptor multisample)
        {
            _currentState.Pipeline.AlphaToCoverageEnable = multisample.AlphaToCoverageEnable;
            _currentState.Pipeline.AlphaToOneEnable = multisample.AlphaToOneEnable;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public void UpdateScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            for (int i = 0; i < regions.Length; i++)
            {
                Rectangle<int> region = regions[i];

                _currentState.Scissors[i] = new MTLScissorRect
                {
                    height = (ulong)region.Height,
                    width = (ulong)region.Width,
                    x = (ulong)region.X,
                    y = (ulong)region.Y
                };
            }

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetScissors(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.Scissors);
        }

        public void UpdateViewports(ReadOnlySpan<Viewport> viewports)
        {
            static float Clamp(float value)
            {
                return Math.Clamp(value, 0f, 1f);
            }

            for (int i = 0; i < viewports.Length; i++)
            {
                Viewport viewport = viewports[i];
                // Y coordinate is inverted
                _currentState.Viewports[i] = new MTLViewport
                {
                    originX = viewport.Region.X,
                    originY = viewport.Region.Y + viewport.Region.Height,
                    width = viewport.Region.Width,
                    height = -viewport.Region.Height,
                    znear = Clamp(viewport.DepthNear),
                    zfar = Clamp(viewport.DepthFar)
                };
            }

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetViewports(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.Viewports);
        }

        public readonly void UpdateVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            for (int i = 0; i < Constants.MaxVertexBuffers; i++)
            {
                if (i < vertexBuffers.Length)
                {
                    VertexBufferDescriptor vertexBuffer = vertexBuffers[i];

                    _currentState.VertexBuffers[i] = new VertexBufferState(
                        vertexBuffer.Buffer.Handle,
                        vertexBuffer.Buffer.Offset,
                        vertexBuffer.Buffer.Size,
                        vertexBuffer.Divisor,
                        vertexBuffer.Stride);
                }
                else
                {
                    _currentState.VertexBuffers[i] = VertexBufferState.Null;
                }
            }

            // Update the buffers on the pipeline
            UpdatePipelineVertexState(_currentState.VertexBuffers, _currentState.VertexAttribs);

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            foreach (BufferAssignment assignment in buffers)
            {
                BufferRange buffer = assignment.Range;
                int index = assignment.Binding;

                Auto<DisposableBuffer> mtlBuffer = buffer.Handle == BufferHandle.Null
                    ? null
                    : _bufferManager.GetBuffer(buffer.Handle, buffer.Write);

                _currentState.UniformBufferRefs[index] = new BufferRef(mtlBuffer, ref buffer);
            }

            SignalDirty(DirtyFlags.Uniforms);
        }

        public readonly void UpdateStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            foreach (BufferAssignment assignment in buffers)
            {
                BufferRange buffer = assignment.Range;
                int index = assignment.Binding;

                Auto<DisposableBuffer> mtlBuffer = buffer.Handle == BufferHandle.Null
                    ? null
                    : _bufferManager.GetBuffer(buffer.Handle, buffer.Write);

                _currentState.StorageBufferRefs[index] = new BufferRef(mtlBuffer, ref buffer);
            }

            SignalDirty(DirtyFlags.Storages);
        }

        public readonly void UpdateStorageBuffers(int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                Auto<DisposableBuffer> mtlBuffer = buffers[i];
                int index = first + i;

                _currentState.StorageBufferRefs[index] = new BufferRef(mtlBuffer);
            }

            SignalDirty(DirtyFlags.Storages);
        }

        public void UpdateCullMode(bool enable, Face face)
        {
            // Metal has no "cull everything" mode, so culling both faces is emulated
            // with an empty scissor rect. The guest's cull face register keeps its
            // value while culling is disabled, so that emulation must only kick in
            // when culling is actually enabled - otherwise geometry drawn double
            // sided (foliage, in particular) is silently scissored away entirely.
            bool cullBoth = enable && face == Face.FrontAndBack;

            bool dirtyScissor = cullBoth != _currentState.CullBoth;

            _currentState.CullMode = enable ? face.Convert() : MTLCullMode.None;
            _currentState.CullBoth = cullBoth;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetCullMode(renderCommandEncoder);
                SetScissors(renderCommandEncoder);
                return;
            }

            // Mark dirty
            SignalDirty(DirtyFlags.CullMode);

            if (dirtyScissor)
            {
                SignalDirty(DirtyFlags.Scissors);
            }
        }

        public readonly void UpdateFrontFace(FrontFace frontFace)
        {
            _currentState.Winding = frontFace.Convert();

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetFrontFace(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.FrontFace);
        }

        private readonly void UpdateStencilRefValue(int frontRef, int backRef)
        {
            _currentState.FrontRefValue = frontRef;
            _currentState.BackRefValue = backRef;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetStencilRefValue(renderCommandEncoder);
            }

            SignalDirty(DirtyFlags.StencilRef);
        }

        public readonly void UpdateTextureAndSampler(ShaderStage stage, int binding, TextureBase texture, SamplerHolder samplerHolder)
        {
            if (texture != null)
            {
                _currentState.TextureRefs[binding] = new(stage, texture, samplerHolder?.GetSampler());
            }
            else
            {
                _currentState.TextureRefs[binding] = default;
            }

            SignalDirty(DirtyFlags.Textures);
        }

        public readonly void UpdateImage(ShaderStage stage, int binding, TextureBase image)
        {
            if (image is Texture view)
            {
                _currentState.ImageRefs[binding] = new(stage, view);
            }
            else
            {
                _currentState.ImageRefs[binding] = default;
            }

            SignalDirty(DirtyFlags.Images);
        }

        public readonly void UpdateTextureArray(ShaderStage stage, int binding, TextureArray array)
        {
            ref EncoderState.ArrayRef<TextureArray> arrayRef = ref GetArrayRef(ref _currentState.TextureArrayRefs, binding, ArrayGrowthSize);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<TextureArray>(stage, array);

                SignalDirty(DirtyFlags.Textures);
            }
        }

        public readonly void UpdateTextureArraySeparate(ShaderStage stage, int setIndex, TextureArray array)
        {
            ref EncoderState.ArrayRef<TextureArray> arrayRef = ref GetArrayRef(ref _currentState.TextureArrayExtraRefs, setIndex - MetalRenderer.TotalSets);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<TextureArray>(stage, array);

                SignalDirty(DirtyFlags.Textures);
            }
        }

        public readonly void UpdateImageArray(ShaderStage stage, int binding, ImageArray array)
        {
            ref EncoderState.ArrayRef<ImageArray> arrayRef = ref GetArrayRef(ref _currentState.ImageArrayRefs, binding, ArrayGrowthSize);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<ImageArray>(stage, array);

                SignalDirty(DirtyFlags.Images);
            }
        }

        public readonly void UpdateImageArraySeparate(ShaderStage stage, int setIndex, ImageArray array)
        {
            ref EncoderState.ArrayRef<ImageArray> arrayRef = ref GetArrayRef(ref _currentState.ImageArrayExtraRefs, setIndex - MetalRenderer.TotalSets);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<ImageArray>(stage, array);

                SignalDirty(DirtyFlags.Images);
            }
        }

        private static ref EncoderState.ArrayRef<T> GetArrayRef<T>(ref EncoderState.ArrayRef<T>[] array, int index, int growthSize = 1)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            if (array.Length <= index)
            {
                Array.Resize(ref array, index + growthSize);
            }

            return ref array[index];
        }

        private readonly void SetDepthStencilState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            MTLDepthStencilState state = DepthStencil != null
                ? _depthStencilCache.GetOrCreate(_currentState.DepthStencilUid)
                : _defaultState;

            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.DepthStencil) ||
                _applied.DepthStencilState != state.NativePtr)
            {
                _applied.DepthStencilState = state.NativePtr;

                renderCommandEncoder.SetDepthStencilState(state);
            }
        }

        private readonly void SetDepthClamp(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.DepthClip) ||
                _applied.DepthClipMode != _currentState.DepthClipMode)
            {
                _applied.DepthClipMode = _currentState.DepthClipMode;

                renderCommandEncoder.SetDepthClipMode(_currentState.DepthClipMode);
            }
        }

        private readonly void SetDepthBias(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.DepthBias) ||
                _applied.DepthBias != _currentState.DepthBias ||
                _applied.SlopeScale != _currentState.SlopeScale ||
                _applied.Clamp != _currentState.Clamp)
            {
                _applied.DepthBias = _currentState.DepthBias;
                _applied.SlopeScale = _currentState.SlopeScale;
                _applied.Clamp = _currentState.Clamp;

                renderCommandEncoder.SetDepthBias(_currentState.DepthBias, _currentState.SlopeScale, _currentState.Clamp);
            }
        }

        // The size of the pass actually being encoded, recorded where the descriptor is
        // built. Deriving it from _currentState instead was wrong twice over: the state
        // can hold targets bound for an earlier pass, and for passes the helper shaders
        // build it holds none at all - in which case the fallback returned ulong.MaxValue
        // and the scissor clamp below became a no-op. Metal's validation layer caught it
        // immediately: 65535x65535 scissor rects against a 1x1 render pass.
        private static ulong _passWidth;
        private static ulong _passHeight;

        internal static void NotePassSize(ulong width, ulong height)
        {
            _passWidth = width;
            _passHeight = height;
        }

        private readonly (ulong Width, ulong Height) GetRenderPassSize()
        {
            if (_passWidth != 0 && _passHeight != 0)
            {
                return (_passWidth, _passHeight);
            }

            for (int i = 0; i < _currentState.RenderTargets.Length; i++)
            {
                if (_currentState.RenderTargets[i] is Texture tex)
                {
                    return ((ulong)tex.Width, (ulong)tex.Height);
                }
            }

            if (_currentState.DepthStencil != null)
            {
                return ((ulong)_currentState.DepthStencil.Width, (ulong)_currentState.DepthStencil.Height);
            }

            // Reaching here means the pass size is unknown from both the descriptor and
            // the bound state, and the caller will then clamp against ulong.MaxValue,
            // which is no clamp at all. Metal's validation layer still reports 65535
            // scissor rects against a 1x1 pass, so one path gets here; name it rather
            // than guess. Once per distinct shape.
            if (_unknownPassSizeReported.Add((_currentState.Scissors.Length, _currentState.RenderTargets.Length)))
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"pass size unknown at scissor time: targets={_currentState.RenderTargets.Length} " +
                    $"depth={(_currentState.DepthStencil != null ? "yes" : "no")} " +
                    $"scissors={_currentState.Scissors.Length} " +
                    $"program={_currentState.RenderProgram?.DebugLabel ?? "<none>"}");
            }

            return (ulong.MaxValue, ulong.MaxValue);
        }

        private static readonly HashSet<(int, int)> _unknownPassSizeReported = [];

        private unsafe void SetScissors(MTLRenderCommandEncoder renderCommandEncoder)
        {
            bool isTriangles = (_currentState.Topology == PrimitiveTopology.Triangles) ||
                               (_currentState.Topology == PrimitiveTopology.TriangleStrip);

            if (_currentState.CullBoth && isTriangles)
            {
                renderCommandEncoder.SetScissorRect(new MTLScissorRect { x = 0, y = 0, width = 0, height = 0 });
            }
            else
            {
                int count = _currentState.Scissors.Length;

                if (count > 0)
                {
                    // Metal requires every scissor rect to lie within the render pass
                    // attachments. The guest freely uses full-surface sentinel values
                    // (65535x65535); passing them through is an API violation and
                    // undefined behaviour without the validation layer - observed as
                    // randomly dropped rasterization in small passes (impostor atlas
                    // bakes). Clamp at apply time against the current pass size; the
                    // unclamped guest values stay in _currentState for the next pass.
                    (ulong passWidth, ulong passHeight) = GetRenderPassSize();

                    // Unknown size means the clamp would run against ulong.MaxValue and
                    // pass the guest's 65535 sentinel straight through - which is what
                    // Metal's validation layer kept reporting against a 1x1 pass even
                    // after the size was taken from the descriptor, because the bound
                    // state is emptied again before this runs. Leaving the scissor unset
                    // is always legal and means the whole attachment, which is what a
                    // full-surface sentinel was asking for anyway.
                    if (passWidth == ulong.MaxValue)
                    {
                        return;
                    }

                    MTLScissorRect* clamped = stackalloc MTLScissorRect[count];

                    for (int i = 0; i < count; i++)
                    {
                        MTLScissorRect rect = _currentState.Scissors[i];

                        rect.x = Math.Min(rect.x, passWidth);
                        rect.y = Math.Min(rect.y, passHeight);
                        rect.width = Math.Min(rect.width, passWidth - rect.x);
                        rect.height = Math.Min(rect.height, passHeight - rect.y);

                        clamped[i] = rect;
                    }

                    renderCommandEncoder.SetScissorRects((IntPtr)clamped, (ulong)count);
                }
            }
        }

        private readonly unsafe void SetViewports(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (_currentState.Viewports.Length > 0)
            {
                fixed (MTLViewport* pMtlViewports = _currentState.Viewports)
                {
                    renderCommandEncoder.SetViewports((IntPtr)pMtlViewports, (ulong)_currentState.Viewports.Length);
                }
            }
        }

        private readonly void UpdatePipelineVertexState(VertexBufferState[] bufferDescriptors, VertexAttribDescriptor[] attribDescriptors)
        {
            ref PipelineState pipeline = ref _currentState.Pipeline;
            uint indexMask = 0;

            for (int i = 0; i < attribDescriptors.Length; i++)
            {
                ref VertexInputAttributeUid attrib = ref pipeline.Internal.VertexAttributes[i];

                if (attribDescriptors[i].IsZero)
                {
                    attrib.Format = attribDescriptors[i].Format.Convert();
                    indexMask |= 1u << (int)Constants.ZeroBufferIndex;
                    attrib.BufferIndex = Constants.ZeroBufferIndex;
                    attrib.Offset = 0;
                }
                else
                {
                    attrib.Format = attribDescriptors[i].Format.Convert();
                    indexMask |= 1u << attribDescriptors[i].BufferIndex;
                    attrib.BufferIndex = (ulong)attribDescriptors[i].BufferIndex;
                    attrib.Offset = (ulong)attribDescriptors[i].Offset;
                }
            }

            for (int i = 0; i < bufferDescriptors.Length; i++)
            {
                ref VertexInputLayoutUid layout = ref pipeline.Internal.VertexBindings[i];

                if ((indexMask & (1u << i)) != 0)
                {
                    layout.Stride = (uint)bufferDescriptors[i].Stride;

                    if (layout.Stride == 0)
                    {
                        layout.Stride = 1;
                        layout.StepFunction = MTLVertexStepFunction.Constant;
                        layout.StepRate = 0;
                    }
                    else
                    {
                        if (bufferDescriptors[i].Divisor > 0)
                        {
                            layout.StepFunction = MTLVertexStepFunction.PerInstance;
                            layout.StepRate = (uint)bufferDescriptors[i].Divisor;
                        }
                        else
                        {
                            layout.StepFunction = MTLVertexStepFunction.PerVertex;
                            layout.StepRate = 1;
                        }
                    }
                }
                else
                {
                    layout = new();
                }
            }

            ref VertexInputLayoutUid zeroBufLayout = ref pipeline.Internal.VertexBindings[(int)Constants.ZeroBufferIndex];

            // Zero buffer
            if ((indexMask & (1u << (int)Constants.ZeroBufferIndex)) != 0)
            {
                zeroBufLayout.Stride = 1;
                zeroBufLayout.StepFunction = MTLVertexStepFunction.Constant;
                zeroBufLayout.StepRate = 0;
            }
            else
            {
                zeroBufLayout = new();
            }

            pipeline.VertexAttributeDescriptionsCount = (uint)attribDescriptors.Length;
            pipeline.VertexBindingDescriptionsCount = Constants.ZeroBufferIndex + 1; // TODO: move this out?
        }

        private readonly void SetVertexBuffers(VertexBufferState[] bufferStates, ref readonly RenderEncoderBindings bindings)
        {
            for (int i = 0; i < bufferStates.Length; i++)
            {
                (MTLBuffer mtlBuffer, int offset) = bufferStates[i].GetVertexBuffer(_bufferManager, _pipeline.Cbs);

                if (mtlBuffer.NativePtr != IntPtr.Zero)
                {
                    bindings.VertexBuffers.Add(new BufferResource(mtlBuffer, (ulong)offset, (ulong)i));
                }
            }

            Auto<DisposableBuffer> autoZeroBuffer = _zeroBuffer == BufferHandle.Null
                ? null
                : _bufferManager.GetBuffer(_zeroBuffer, false);

            if (autoZeroBuffer == null)
            {
                return;
            }

            MTLBuffer zeroMtlBuffer = autoZeroBuffer.Get(_pipeline.Cbs).Value;
            bindings.VertexBuffers.Add(new BufferResource(zeroMtlBuffer, 0, Constants.ZeroBufferIndex));
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForBuffer(ref BufferRef buffer)
        {
            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            BufferRange? range = buffer.Range;
            Auto<DisposableBuffer> autoBuffer = buffer.Buffer;

            if (autoBuffer != null)
            {
                int offset = 0;
                MTLBuffer mtlBuffer;

                if (range.HasValue)
                {
                    offset = range.Value.Offset;

                    if (range.Value.Write)
                    {
                        mtlBuffer = autoBuffer.Get(_pipeline.Cbs, offset, range.Value.Size, true).Value;
                    }
                    else
                    {
                        // A read only binding may be served from a mirror holding writes
                        // that have not been put into the buffer itself, which is what
                        // keeps those writes from needing a blit inside the render pass.
                        mtlBuffer = autoBuffer.GetMirrorable(_pipeline.Cbs, ref offset, range.Value.Size, out _).Value;
                    }
                }
                else
                {
                    mtlBuffer = autoBuffer.Get(_pipeline.Cbs).Value;
                }

                gpuAddress = mtlBuffer.GpuAddress + (ulong)offset;
                nativePtr = mtlBuffer.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        /// <summary>
        /// Which program's texture bindings the input report follows.
        ///
        /// It was hardcoded to 3ebc3a8f6b77cc8f - the tonemap - in three places, while the
        /// shader being measured in-shader all session is ee89b4e471373459, the composite.
        /// So every "WHITE inputs" line described a different program's bindings than the
        /// fetch measurement it was being read alongside, and the two were never about the
        /// same texture.
        /// </summary>
        private static readonly string _inputWatchLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_INPUT_WATCH") ?? "3ebc3a8f6b77cc8f";

        // Hot-swappable so both arms run inside one session with everything else equal.
        // The first validation of this change compared arms that differed in their probe
        // environment as well, and the 0% it reported was the probes' doing, not the fix's
        // - the exact single-variable rule these notes keep re-learning.
        private static bool _dedupByReferenceOnly = true;

        internal static void RefreshDedupToggle()
        {
            // Default ON (reference-only, the original behaviour). The storage-identity
            // comparison measured no effect on the flash in a clean in-session A/B, and
            // with it active the game aborts in Metal validation on camera movement -
            // IOGPUMetalCommandBuffer validate -> MTLReportFailure -> abort - which is the
            // signature of an attachment nulled while the pipeline still declares it.
            // Opt back in with /tmp/ryujinx-metal-dedup-by-storage=1 for experiments only.
            try
            {
                _dedupByReferenceOnly = !(System.IO.File.Exists("/tmp/ryujinx-metal-dedup-by-storage") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-dedup-by-storage").Trim() == "1");
            }
            catch (System.IO.IOException)
            {
            }
        }

        private static bool _identitySampling;

        /// <summary>
        /// Declare residency on a sampled texture's parent rather than on the view.
        ///
        /// Sampled textures reach the shader through an argument buffer, so the encoder has
        /// to be told about them with useResource, and this backend passes the handle it
        /// bound - which for a view is a distinct MTLTexture from the one the render pass
        /// wrote as an attachment. Metal tracks residency and hazards against the
        /// underlying allocation, and Apple's guidance for texture views is to declare the
        /// parent; naming only the view can leave the dependency on those earlier writes
        /// unestablished.
        ///
        /// The composite's input is exactly that shape - the log shows handle 0xB910A4000
        /// against root 0xB9109BC00, so it is sampled through a view of the texture the
        /// scene passes wrote. And the failure this predicts is the one measured: reading
        /// content from before the writes landed, which is uniform, on a Metal backend
        /// only, intermittently, and beyond the reach of the guest's own barriers - the
        /// last of which was ruled out tonight at about 1300 samples an arm.
        ///
        /// The argument buffer still gets the view's GPU resource ID, so format and swizzle
        /// are unaffected. Only what is named for residency changes.
        ///
        /// Hot-swapped through /tmp/ryujinx-metal-parent-residency so both arms can be
        /// measured inside one reproduction window.
        /// </summary>
        private static bool _residencyOnParent;

        /// <summary>
        /// The verdict experiment for the zero-draw Load/Store pass, and its fix if it
        /// holds. Passes are created with StoreAction Unknown and the real store action is
        /// chosen when the pass ends: Store normally, DontCare when the pass encoded no
        /// draws and cleared nothing - a pass like that is a pure tile round trip, and the
        /// white flip interval contains exactly one of them and nothing else that writes.
        /// /tmp/ryujinx-metal-elide-empty-store, re-read once a frame; the per-pass flag
        /// remembers which convention the descriptor was built with, because a pass
        /// created with a fixed action must not be fixed up.
        /// </summary>
        private static bool _elideEmptyStore;

        private static bool _passStoreUnknown;
        private static ulong _passColorMask;
        private static bool _passHasDepth;
        private static bool _passHasStencil;
        private static bool _passCleared;

        // Whether this pass's colour target 0 is the scene-class texture the flash lives
        // on. Blanket elision wedged the guest - some empty pass elsewhere is load-bearing
        // - so the DontCare is confined to the class where the crystallisation was proven.
        private static bool _passSceneClass;

        internal static void RefreshSamplingToggle()
        {
            try
            {
                _identitySampling = System.IO.File.Exists("/tmp/ryujinx-metal-identity-sample") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-identity-sample").Trim() == "1";

                _residencyOnParent = System.IO.File.Exists("/tmp/ryujinx-metal-parent-residency") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-parent-residency").Trim() == "1";

                _elideEmptyStore = System.IO.File.Exists("/tmp/ryujinx-metal-elide-empty-store") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-elide-empty-store").Trim() == "1";
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        /// <summary>
        /// Sampling a texture that is simultaneously a colour attachment is undefined in
        /// Metal, and undefined reads on this hardware come back near-white - the flash's
        /// exact signature. It also fits every other constraint at once: no write happens
        /// so every write hook is blind, it is intermittent because it depends on whether
        /// the guest's barrier was honoured, and MoltenVK does not expose it because
        /// Vulkan's own feedback rules force a different structure. This backend skips 89
        /// guest barriers per frame as no-ops, which is exactly the population that could
        /// leave a sampled texture still attached.
        /// </summary>
        /// <summary>
        /// Whether any texture currently bound for sampling is also an attachment of the
        /// pass that is open. Reads bound state only, so it can be answered before
        /// RenderResourcesPrepass runs - which is the whole point: the previous version
        /// answered it during the prepass and had to end the pass from inside encoder
        /// acquisition, which faulted the driver.
        /// </summary>
        public readonly bool SamplesOwnAttachment()
        {
            if (!FeedbackProbe.Enabled)
            {
                return false;
            }

            bool any = false;

            foreach (TextureRef reference in _currentState.TextureRefs)
            {
                if (reference.Storage is Texture sampled && FeedbackProbe.CheckAgainstLiveAttachments(sampled))
                {
                    any = true;
                }
            }

            return any;
        }

        /// <summary>
        /// Storage written as a colour attachment since the current command buffer began,
        /// and whether anything now bound for sampling is in that set.
        ///
        /// This is where the two backends part company. Ryujinx's Vulkan path issues real
        /// pipeline barriers, and MoltenVK has nowhere to put a read-after-write barrier
        /// inside a Metal render encoder, so it ends the encoder - Vulkan therefore splits
        /// the pass at every read-after-write, automatically. This backend issues no
        /// barriers at all and leans entirely on Metal's automatic hazard tracking, which
        /// for argument-buffer reads is fed by useResource rather than by anything the
        /// driver can see directly. Same machine, same driver, same save, argument buffers
        /// on in both: Vulkan 0 flat frames in 120 samples, Metal about 40%.
        ///
        /// The ledger's "strict barrier" test keyed on guest TextureBarrier calls, which
        /// this game issues rarely; this keys on the hazard actually occurring.
        /// </summary>
        private readonly HashSet<IntPtr> _writtenThisCb = new();

        public readonly void NoteAttachmentWritten(IntPtr root)
        {
            if (root != IntPtr.Zero)
            {
                _writtenThisCb.Add(root);
            }
        }

        public readonly void ClearWrittenThisCb()
        {
            _writtenThisCb.Clear();
        }

        public readonly bool SamplesEarlierWrite()
        {
            if (_writtenThisCb.Count == 0)
            {
                return false;
            }

            foreach (TextureRef reference in _currentState.TextureRefs)
            {
                if (reference.Storage is Texture sampled && _writtenThisCb.Contains(sampled.CanonicalPtr))
                {
                    return true;
                }
            }

            foreach (EncoderState.ArrayRef<TextureArray> arrayRef in _currentState.TextureArrayRefs)
            {
                if (arrayRef.Array == null)
                {
                    continue;
                }

                foreach (TextureRef reference in arrayRef.Array.GetTextureRefs())
                {
                    if (reference.Storage is Texture sampled && _writtenThisCb.Contains(sampled.CanonicalPtr))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The scene-class texture currently bound for sampling, if any. Bound state only,
        /// so it can be answered before RenderResourcesPrepass and before any encoder is
        /// acquired - the same constraint the feedback split had to learn the hard way.
        /// </summary>
        public readonly Texture SceneClassSampledTexture()
        {
            foreach (TextureRef reference in _currentState.TextureRefs)
            {
                if (reference.Storage is Texture sampled && Texture.IsSceneClass(sampled.Info))
                {
                    return sampled;
                }
            }

            // Textures also arrive as arrays, and a walk of TextureRefs alone silently
            // misses those - which is exactly the shape of every "nothing is bound here"
            // artefact in this investigation's history.
            foreach (EncoderState.ArrayRef<TextureArray> arrayRef in _currentState.TextureArrayRefs)
            {
                if (arrayRef.Array == null)
                {
                    continue;
                }

                foreach (TextureRef reference in arrayRef.Array.GetTextureRefs())
                {
                    if (reference.Storage is Texture sampled && Texture.IsSceneClass(sampled.Info))
                    {
                        return sampled;
                    }
                }
            }

            return null;
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForTexture(ref TextureRef texture)
        {
            TextureBase storage = texture.Storage;

            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (storage != null)
            {
                if (storage is TextureBuffer textureBuffer)
                {
                    textureBuffer.RebuildStorage(false);
                }

                // A/B switch: sampling normally goes through the swizzled view, while
                // attachments use the identity handle. RG11B10Float carries no alpha, so
                // a swizzle that routes a component to alpha or to one samples as exactly
                // 1.0 - which is the value the tonemap reads on a white frame.
                MTLTexture mtlTexture = _identitySampling
                    ? storage.GetIdentityHandle(_pipeline.Cbs)
                    : storage.GetHandle(_pipeline.Cbs);

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForImage(ref ImageRef image)
        {
            Texture storage = image.Storage;

            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (storage != null)
            {
                // A/B switch: sampling normally goes through the swizzled view, while
                // attachments use the identity handle. RG11B10Float carries no alpha, so
                // a swizzle that routes a component to alpha or to one samples as exactly
                // 1.0 - which is the value the tonemap reads on a white frame.
                MTLTexture mtlTexture = _identitySampling
                    ? storage.GetIdentityHandle(_pipeline.Cbs)
                    : storage.GetHandle(_pipeline.Cbs);

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;

                // The view carries the format and swizzle the shader needs, so it stays in
                // the argument buffer; only the resource named for residency changes.
                if (_residencyOnParent && storage is Texture t && t.CanonicalPtr != IntPtr.Zero)
                {
                    nativePtr = t.CanonicalPtr;
                }
            }

            return (gpuAddress, nativePtr);
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForTextureBuffer(ref TextureBuffer bufferTexture)
        {
            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (bufferTexture != null)
            {
                bufferTexture.RebuildStorage(false);

                MTLTexture mtlTexture = bufferTexture.GetHandle(_pipeline.Cbs);

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddResource(IntPtr resourcePointer, MTLResourceUsage usage, MTLRenderStages stages, ref readonly RenderEncoderBindings bindings)
        {
            if (resourcePointer != IntPtr.Zero)
            {
                bindings.Resources.Add(new Resource(new MTLResource(resourcePointer), usage, stages));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddResource(IntPtr resourcePointer, MTLResourceUsage usage, ref readonly ComputeEncoderBindings bindings)
        {
            if (resourcePointer != IntPtr.Zero)
            {
                bindings.Resources.Add(new Resource(new MTLResource(resourcePointer), usage, 0));
            }
        }

        private readonly void UpdateAndBind(Program program, uint setIndex, ref readonly RenderEncoderBindings bindings)
        {
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];
            bool captureToneMapBindings = ToneMapProbe.IsWatched(program.DebugLabel);

            if (bindingSegments.Length == 0)
            {
                return;
            }

            ScopedTemporaryBuffer vertArgBuffer = default;
            ScopedTemporaryBuffer fragArgBuffer = default;

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                vertArgBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.ArgumentBufferSizes[setIndex] * sizeof(ulong), true);
                bindings.TemporaryBuffers.Add(vertArgBuffer);
            }

            if (program.FragArgumentBufferSizes[setIndex] > 0)
            {
                fragArgBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.FragArgumentBufferSizes[setIndex] * sizeof(ulong), true);
                bindings.TemporaryBuffers.Add(fragArgBuffer);
            }

            Span<ulong> vertResourceIds = stackalloc ulong[program.ArgumentBufferSizes[setIndex]];
            Span<ulong> fragResourceIds = stackalloc ulong[program.FragArgumentBufferSizes[setIndex]];

            int vertResourceIdIndex = 0;
            int fragResourceIdIndex = 0;

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                switch (setIndex)
                {
                    case Constants.ConstantBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.UniformBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            // Contents, not identity. The composite's weight sum can be
                            // driven to zero by its constants alone, and identity is what
                            // every previous binding check compared.
                            if (UploadCorrelator.Enabled && buffer.Buffer != null &&
                                program.IsTexelFetchComposite)
                            {
                                MTLBuffer cb = buffer.Buffer.GetUnsafe().Value;
                                UploadCorrelator.NoteCompositeConstants(
                                    index, cb.Contents, (int)(buffer.Range?.Offset ?? 0));
                            }

                            if (HdrPassProbe.Enabled && buffer.Buffer != null && index == 20 &&
                                program.DebugLabel == _inputWatchLabel)
                            {
                                MTLBuffer tb = buffer.Buffer.GetUnsafe().Value;
                                HdrPassProbe.NoteToneMapWeights(tb.Contents, buffer.Range?.Offset ?? 0);
                            }

                            if (HdrPassProbe.Enabled && buffer.Buffer != null &&
                                HdrPassProbe.IsWatchedTarget(_currentState.RenderTargets[0]))
                            {
                                MTLBuffer ub = buffer.Buffer.GetUnsafe().Value;
                                HdrPassProbe.NoteUniform(ub.Contents, buffer.Range?.Offset ?? 0, buffer.Range?.Size ?? 256);
                            }

                            if (captureToneMapBindings)
                            {
                                if (index == 20)
                                {
                                    _currentState.DrawRingCb1Address = gpuAddress;
                                }
                                else if (index == 22)
                                {
                                    _currentState.DrawRingCb3Address = gpuAddress;
                                }
                            }

                            MTLRenderStages renderStages = 0;

                            if ((segment.Stages & ResourceStages.Vertex) != 0)
                            {
                                vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                vertResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageVertex;
                            }

                            if ((segment.Stages & ResourceStages.Fragment) != 0)
                            {
                                fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                fragResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageFragment;
                            }

                            AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                        }
                        break;
                    case Constants.StorageBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.StorageBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            MTLRenderStages renderStages = 0;

                            if ((segment.Stages & ResourceStages.Vertex) != 0)
                            {
                                vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                vertResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageVertex;
                            }

                            if ((segment.Stages & ResourceStages.Fragment) != 0)
                            {
                                fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                fragResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageFragment;
                            }

                            // Storage buffers are writable from graphics stages too, so
                            // the residency declaration must include Write - writing to a
                            // resource declared read-only is undefined behaviour. The
                            // compute path and the image case below already declare
                            // Read | Write; only this one was left read-only.
                            AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                        }
                        break;
                    case Constants.TexturesSetIndex:
                        if (!segment.IsArray)
                        {
                            bool hasTexture = segment.Type != ResourceType.Sampler;
                            bool hasSampler = segment.Type is ResourceType.Sampler or ResourceType.TextureAndSampler;

                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref TextureRef texture = ref _currentState.TextureRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = hasTexture
                                    ? AddressForTexture(ref texture)
                                    : (0, IntPtr.Zero);

                                // The resource id actually handed to the shader for the
                                // scene texture. Constant injection proved the fetch
                                // returns something that is not in that texture's memory,
                                // which is either the driver misreading it or the shader
                                // being pointed somewhere else entirely; this is the only
                                // number that separates the two.
                                if (UploadCorrelator.Enabled && hasTexture &&
                                    texture.Storage is Texture sceneCandidate &&
                                    Texture.IsSceneClass(sceneCandidate.Info))
                                {
                                    UploadCorrelator.NoteSceneBinding(
                                        gpuAddress, nativePtr, sceneCandidate.CanonicalPtr, program.DebugLabel,
                                        null);

                                    // Sample what the composite WRITES, not what it reads.
                                    if (program.IsTexelFetchComposite &&
                                        _currentState.RenderTargets is { Length: > 0 } &&
                                        _currentState.RenderTargets[0] != null)
                                    {
                                        UploadCorrelator.NoteCompositeOutput(_currentState.RenderTargets[0]);
                                    }
                                }

                                if (HdrPassProbe.Enabled && hasTexture &&
                                    HdrPassProbe.IsWatchedTarget(_currentState.RenderTargets[0]))
                                {
                                    HdrPassProbe.NoteSampled(nativePtr, texture.Storage);
                                }

                                if (HdrPassProbe.Enabled && hasTexture)
                                {
                                    HdrPassProbe.NoteSampledUnderWatchedTarget(
                                        index, texture.Storage, _currentState.RenderTargets[0]);

                                    if (_currentState.RenderTargets[0] is Texture spanRt &&
                                        texture.Storage != null)
                                    {
                                        HdrPassProbe.NoteSpan(
                                            program.DebugLabel,
                                            texture.Storage.Width, texture.Storage.Height,
                                            spanRt.Width, spanRt.Height);
                                    }

                                    HdrPassProbe.NoteCompositeDraw(
                                        program.DebugLabel, _currentState.RenderTargets[0]);

                                    if (program.DebugLabel == _inputWatchLabel)
                                    {
                                        HdrPassProbe.NoteToneMapSlotId(index, gpuAddress, texture.Storage);
                                    }
                                }

                                if (captureToneMapBindings && hasTexture)
                                {
                                    if (index == 8)
                                    {
                                        _currentState.DrawRingTex8ResourceId = gpuAddress;
                                    }
                                    else if (index == 10)
                                    {
                                        _currentState.DrawRingTexAResourceId = gpuAddress;
                                    }
                                }

                                ulong samplerId = hasSampler && texture.Sampler != null
                                    ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                    : 0;

                                MTLRenderStages renderStages = 0;

                                if ((segment.Stages & ResourceStages.Vertex) != 0)
                                {
                                    if (hasTexture)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;
                                    }

                                    if (hasSampler)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = samplerId;
                                        vertResourceIdIndex++;
                                    }

                                    renderStages |= MTLRenderStages.RenderStageVertex;
                                }

                                if ((segment.Stages & ResourceStages.Fragment) != 0)
                                {
                                    if (hasTexture)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                    }

                                    if (hasSampler)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = samplerId;
                                        fragResourceIdIndex++;
                                    }

                                    renderStages |= MTLRenderStages.RenderStageFragment;
                                }

                                if (hasTexture)
                                {
                                    AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                                }
                            }
                        }
                        else
                        {
                            TextureArray textureArray = _currentState.TextureArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferTexture)
                            {
                                TextureRef[] textures = textureArray.GetTextureRefs();
                                bool hasTexture = segment.Type != ResourceType.Sampler;
                                bool hasSampler = segment.Type is ResourceType.Sampler or ResourceType.TextureAndSampler;

                                for (int i = 0; i < textures.Length; i++)
                                {
                                    TextureRef texture = textures[i];

                                    // The tonemap's inputs were only ever recorded from the
                                    // non-array branch. If they actually arrive through an
                                    // array segment, everything compared so far was a
                                    // different texture than the one the shader samples.
                                    if (HdrPassProbe.Enabled && hasTexture &&
                                        program.DebugLabel == _inputWatchLabel)
                                    {
                                        HdrPassProbe.NoteToneMapInput(1000 + binding + i, texture.Storage);
                                    }

                                    (ulong gpuAddress, IntPtr nativePtr) = hasTexture
                                        ? AddressForTexture(ref texture)
                                        : (0, IntPtr.Zero);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        if (hasTexture)
                                        {
                                            vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                            vertResourceIdIndex++;
                                        }

                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        if (hasTexture)
                                        {
                                            fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                            fragResourceIdIndex++;
                                        }

                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    if (hasTexture)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                                    }
                                }

                                if (hasSampler)
                                {
                                    foreach (TextureRef texture in textures)
                                    {
                                        ulong gpuAddress = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;

                                        if ((segment.Stages & ResourceStages.Vertex) != 0)
                                        {
                                            vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                            vertResourceIdIndex++;
                                        }

                                        if ((segment.Stages & ResourceStages.Fragment) != 0)
                                        {
                                            fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                            fragResourceIdIndex++;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferTextures = textureArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferTextures.Length; i++)
                                {
                                    TextureBuffer bufferTexture = bufferTextures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref bufferTexture);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;

                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;

                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                                }
                            }
                        }
                        break;
                    case Constants.ImagesSetIndex:
                        if (!segment.IsArray)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref ImageRef image = ref _currentState.ImageRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = AddressForImage(ref image);

                                MTLRenderStages renderStages = 0;

                                if ((segment.Stages & ResourceStages.Vertex) != 0)
                                {
                                    vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                    vertResourceIdIndex++;
                                    renderStages |= MTLRenderStages.RenderStageVertex;
                                }

                                if ((segment.Stages & ResourceStages.Fragment) != 0)
                                {
                                    fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                    fragResourceIdIndex++;
                                    renderStages |= MTLRenderStages.RenderStageFragment;
                                }
                                AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                            }
                        }
                        else
                        {
                            ImageArray imageArray = _currentState.ImageArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferImage)
                            {
                                TextureRef[] images = imageArray.GetTextureRefs();

                                for (int i = 0; i < images.Length; i++)
                                {
                                    TextureRef image = images[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref image);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferImages = imageArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferImages.Length; i++)
                                {
                                    TextureBuffer image = bufferImages[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref image);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                                }
                            }
                        }
                        break;
                }
            }

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                if (UploadCorrelator.Enabled && program.IsTexelFetchComposite)
                {
                    UploadCorrelator.NoteResidency(bindings.Resources.Count);
                }

                vertArgBuffer.Holder.SetDataUnchecked(vertArgBuffer.Offset, MemoryMarshal.AsBytes(vertResourceIds));
                MTLBuffer mtlVertArgBuffer = _bufferManager.GetBuffer(vertArgBuffer.Handle, false).Get(_pipeline.Cbs).Value;
                bindings.VertexBuffers.Add(new BufferResource(mtlVertArgBuffer, (uint)vertArgBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }

            if (program.FragArgumentBufferSizes[setIndex] > 0)
            {
                fragArgBuffer.Holder.SetDataUnchecked(fragArgBuffer.Offset, MemoryMarshal.AsBytes(fragResourceIds));
                MTLBuffer mtlFragArgBuffer = _bufferManager.GetBuffer(fragArgBuffer.Handle, false).Get(_pipeline.Cbs).Value;

                if (UploadCorrelator.Enabled && program.IsTexelFetchComposite)
                {
                    UploadCorrelator.NoteArgBuffer(
                        mtlFragArgBuffer.Contents, fragArgBuffer.Range.Offset, fragResourceIds);
                }
                bindings.FragmentBuffers.Add(new BufferResource(mtlFragArgBuffer, (uint)fragArgBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }
        }

        private readonly void UpdateAndBind(Program program, uint setIndex, ref readonly ComputeEncoderBindings bindings)
        {
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];

            if (bindingSegments.Length == 0)
            {
                return;
            }

            ScopedTemporaryBuffer argBuffer = default;

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                argBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.ArgumentBufferSizes[setIndex] * sizeof(ulong));
                bindings.TemporaryBuffers.Add(argBuffer);
            }

            Span<ulong> resourceIds = stackalloc ulong[program.ArgumentBufferSizes[setIndex]];
            int resourceIdIndex = 0;

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                switch (setIndex)
                {
                    case Constants.ConstantBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.UniformBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            if ((segment.Stages & ResourceStages.Compute) != 0)
                            {
                                AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                resourceIds[resourceIdIndex] = gpuAddress;
                                resourceIdIndex++;
                            }
                        }
                        break;
                    case Constants.StorageBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.StorageBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            if ((segment.Stages & ResourceStages.Compute) != 0)
                            {
                                AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                resourceIds[resourceIdIndex] = gpuAddress;
                                resourceIdIndex++;
                            }
                        }
                        break;
                    case Constants.TexturesSetIndex:
                        if (!segment.IsArray)
                        {
                            bool hasTexture = segment.Type != ResourceType.Sampler;
                            bool hasSampler = segment.Type is ResourceType.Sampler or ResourceType.TextureAndSampler;

                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref TextureRef texture = ref _currentState.TextureRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = hasTexture
                                    ? AddressForTexture(ref texture)
                                    : (0, IntPtr.Zero);

                                if ((segment.Stages & ResourceStages.Compute) != 0)
                                {
                                    if (hasTexture)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }

                                    if (hasSampler)
                                    {
                                        resourceIds[resourceIdIndex] = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            TextureArray textureArray = _currentState.TextureArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferTexture)
                            {
                                TextureRef[] textures = textureArray.GetTextureRefs();
                                bool hasTexture = segment.Type != ResourceType.Sampler;
                                bool hasSampler = segment.Type is ResourceType.Sampler or ResourceType.TextureAndSampler;

                                for (int i = 0; i < textures.Length; i++)
                                {
                                    TextureRef texture = textures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = hasTexture
                                        ? AddressForTexture(ref texture)
                                        : (0, IntPtr.Zero);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        if (hasTexture)
                                        {
                                            AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                            resourceIds[resourceIdIndex] = gpuAddress;
                                            resourceIdIndex++;
                                        }

                                    }
                                }

                                if (hasSampler)
                                {
                                    foreach (TextureRef texture in textures)
                                    {
                                        resourceIds[resourceIdIndex] = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferTextures = textureArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferTextures.Length; i++)
                                {
                                    TextureBuffer bufferTexture = bufferTextures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref bufferTexture);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        break;
                    case Constants.ImagesSetIndex:
                        if (!segment.IsArray)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref ImageRef image = ref _currentState.ImageRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = AddressForImage(ref image);

                                if ((segment.Stages & ResourceStages.Compute) != 0)
                                {
                                    HdrPassProbe.NoteComputeImage(image.Storage);
                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                    resourceIds[resourceIdIndex] = gpuAddress;
                                    resourceIdIndex++;
                                }
                            }
                        }
                        else
                        {
                            ImageArray imageArray = _currentState.ImageArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferImage)
                            {
                                TextureRef[] images = imageArray.GetTextureRefs();

                                for (int i = 0; i < images.Length; i++)
                                {
                                    TextureRef image = images[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref image);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        HdrPassProbe.NoteComputeImage(image.Storage as Texture);
                                        AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferImages = imageArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferImages.Length; i++)
                                {
                                    TextureBuffer image = bufferImages[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref image);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        break;
                }
            }

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                argBuffer.Holder.SetDataUnchecked(argBuffer.Offset, MemoryMarshal.AsBytes(resourceIds));
                MTLBuffer mtlArgBuffer = _bufferManager.GetBuffer(argBuffer.Handle, false).Get(_pipeline.Cbs).Value;
                bindings.Buffers.Add(new BufferResource(mtlArgBuffer, (uint)argBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }
        }

        private static uint SetIndexToBindingIndex(uint setIndex)
        {
            return setIndex switch
            {
                Constants.ConstantBuffersSetIndex => Constants.ConstantBuffersIndex,
                Constants.StorageBuffersSetIndex => Constants.StorageBuffersIndex,
                Constants.TexturesSetIndex => Constants.TexturesIndex,
                Constants.ImagesSetIndex => Constants.ImagesIndex,
                _ => throw new NotImplementedException()
            };
        }

        private readonly void SetCullMode(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.Cull) ||
                _applied.CullMode != _currentState.CullMode)
            {
                _applied.CullMode = _currentState.CullMode;

                renderCommandEncoder.SetCullMode(_currentState.CullMode);
            }
        }

        private readonly void SetFrontFace(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.Winding) ||
                _applied.Winding != _currentState.Winding)
            {
                _applied.Winding = _currentState.Winding;

                renderCommandEncoder.SetFrontFacingWinding(_currentState.Winding);
            }
        }

        private readonly void SetStencilRefValue(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.StencilRef) ||
                _applied.FrontRefValue != _currentState.FrontRefValue ||
                _applied.BackRefValue != _currentState.BackRefValue)
            {
                _applied.FrontRefValue = _currentState.FrontRefValue;
                _applied.BackRefValue = _currentState.BackRefValue;

                renderCommandEncoder.SetStencilReferenceValues((uint)_currentState.FrontRefValue, (uint)_currentState.BackRefValue);
            }
        }
    }
}
