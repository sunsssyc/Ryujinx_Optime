using Ryujinx.Graphics.Metal;
using SharpMetal.Metal;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

interface IEncoderFactory
{
    MTLRenderCommandEncoder CreateRenderCommandEncoder();
    MTLComputeCommandEncoder CreateComputeCommandEncoder();

    /// <summary>
    /// Resolve deferred store actions on a render encoder that is about to end.
    /// </summary>
    void FixupStoreActions(MTLRenderCommandEncoder encoder);

    /// <summary>
    /// Diagnostic: called whenever a render pass actually ends, so every path that
    /// splits one is accounted for, including those that bypass the pipeline.
    /// </summary>
    void OnRenderPassEnded(EncoderType startingType);
}

/// <summary>
/// Tracks active encoder object for a command buffer.
/// </summary>
[SupportedOSPlatform("macos")]
class CommandBufferEncoder
{
    public EncoderType CurrentEncoderType { get; private set; } = EncoderType.None;

    public MTLBlitCommandEncoder BlitEncoder => new(CurrentEncoder.Value);

    public MTLComputeCommandEncoder ComputeEncoder => new(CurrentEncoder.Value);

    public MTLRenderCommandEncoder RenderEncoder => new(CurrentEncoder.Value);

    internal MTLCommandEncoder? CurrentEncoder { get; private set; }

    private static long _renderEncoderGeneration;

    /// <summary>
    /// Incremented for every render command encoder created, process-wide.
    ///
    /// Encoders are released as soon as their pass ends, so the allocator readily hands
    /// the same address straight back for the next one. Any cache that identifies an
    /// encoder by its pointer alone therefore cannot tell a fresh encoder from the one
    /// it replaced, and would wrongly consider its state already applied - leaving the
    /// new encoder without a pipeline and faulting the driver on the first draw. Pairing
    /// the pointer with this counter makes each encoder distinct.
    /// </summary>
    internal static long RenderEncoderGeneration => System.Threading.Volatile.Read(ref _renderEncoderGeneration);

    private MTLCommandBuffer _commandBuffer;
    private IEncoderFactory _encoderFactory;

    // What the render pass is being ended for, so the diagnostic can tell a split
    // caused by a blit or a dispatch apart from a deliberate end.
    private EncoderType _endingFor = EncoderType.None;

    public void Initialize(MTLCommandBuffer commandBuffer, IEncoderFactory encoderFactory)
    {
        _commandBuffer = commandBuffer;
        _encoderFactory = encoderFactory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MTLRenderCommandEncoder EnsureRenderEncoder()
    {
        if (CurrentEncoderType != EncoderType.Render)
        {
            return BeginRenderPass();
        }

        return RenderEncoder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MTLBlitCommandEncoder EnsureBlitEncoder()
    {
        if (CurrentEncoderType != EncoderType.Blit)
        {
            return BeginBlitPass();
        }

        return BlitEncoder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MTLComputeCommandEncoder EnsureComputeEncoder()
    {
        if (CurrentEncoderType != EncoderType.Compute)
        {
            return BeginComputePass();
        }

        return ComputeEncoder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetRenderEncoder(out MTLRenderCommandEncoder encoder)
    {
        if (CurrentEncoderType != EncoderType.Render)
        {
            encoder = default;
            return false;
        }

        encoder = RenderEncoder;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetBlitEncoder(out MTLBlitCommandEncoder encoder)
    {
        if (CurrentEncoderType != EncoderType.Blit)
        {
            encoder = default;
            return false;
        }

        encoder = BlitEncoder;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetComputeEncoder(out MTLComputeCommandEncoder encoder)
    {
        if (CurrentEncoderType != EncoderType.Compute)
        {
            encoder = default;
            return false;
        }

        encoder = ComputeEncoder;
        return true;
    }

    public void EndCurrentPass()
    {
        if (CurrentEncoder != null)
        {
            switch (CurrentEncoderType)
            {
                case EncoderType.Blit:
                    BlitEncoder.EndEncoding();
                    ObjcOwnership.Release(BlitEncoder.NativePtr);
                    CurrentEncoder = null;
                    break;
                case EncoderType.Compute:
                    ComputeEncoder.EndEncoding();
                    ObjcOwnership.Release(ComputeEncoder.NativePtr);
                    CurrentEncoder = null;
                    break;
                case EncoderType.Render:
                    _encoderFactory?.FixupStoreActions(RenderEncoder);
                    RenderEncoder.EndEncoding();
                    ObjcOwnership.Release(RenderEncoder.NativePtr);
                    CurrentEncoder = null;
                    _encoderFactory?.OnRenderPassEnded(_endingFor);
                    break;
                default:
                    throw new InvalidOperationException();
            }

            CurrentEncoderType = EncoderType.None;
        }
    }

    private MTLRenderCommandEncoder BeginRenderPass()
    {
        _endingFor = EncoderType.Render;
        EndCurrentPass();
        _endingFor = EncoderType.None;

        MTLRenderCommandEncoder renderCommandEncoder = _encoderFactory.CreateRenderCommandEncoder();

        System.Threading.Interlocked.Increment(ref _renderEncoderGeneration);

        // Pass encoders are autoreleased with no pool on this thread; own them
        // for the pass lifetime (released in EndCurrentPass after EndEncoding).
        ObjcOwnership.Retain(renderCommandEncoder.NativePtr);

        CurrentEncoder = renderCommandEncoder;
        CurrentEncoderType = EncoderType.Render;

        return renderCommandEncoder;
    }

    // Diagnostic: RYUJINX_METAL_BLIT_TRACE=1 records which caller forced a blit
    // encoder while a render pass was live. Blits are the single largest cause of
    // render pass splits, and the fix differs per call site.
    private static readonly bool _blitTrace =
        Environment.GetEnvironmentVariable("RYUJINX_METAL_BLIT_TRACE") == "1";

    private static readonly System.Collections.Generic.Dictionary<string, int> _blitCallers = [];

    public static string TakeBlitCallers()
    {
        lock (_blitCallers)
        {
            if (_blitCallers.Count == 0)
            {
                return null;
            }

            string text = string.Join(", ", _blitCallers
                .OrderByDescending(entry => entry.Value)
                .Take(6)
                .Select(entry => $"{entry.Key}={entry.Value}"));

            _blitCallers.Clear();

            return text;
        }
    }

    private MTLBlitCommandEncoder BeginBlitPass()
    {
        if (_blitTrace && CurrentEncoderType == EncoderType.Render)
        {
            System.Diagnostics.StackTrace trace = new(1, false);
            string name = "unknown";

            for (int i = 0; i < trace.FrameCount; i++)
            {
                System.Reflection.MethodBase method = trace.GetFrame(i)?.GetMethod();

                if (method != null && method.DeclaringType?.Name != nameof(CommandBufferEncoder))
                {
                    name = $"{method.DeclaringType?.Name}.{method.Name}";
                    break;
                }
            }

            lock (_blitCallers)
            {
                _blitCallers.TryGetValue(name, out int count);
                _blitCallers[name] = count + 1;
            }
        }

        _endingFor = EncoderType.Blit;
        EndCurrentPass();
        _endingFor = EncoderType.None;

        using MTLBlitPassDescriptor descriptor = new();
        MTLBlitCommandEncoder blitCommandEncoder = _commandBuffer.BlitCommandEncoder(descriptor);

        ObjcOwnership.Retain(blitCommandEncoder.NativePtr);

        CurrentEncoder = blitCommandEncoder;
        CurrentEncoderType = EncoderType.Blit;
        return blitCommandEncoder;
    }

    private MTLComputeCommandEncoder BeginComputePass()
    {
        _endingFor = EncoderType.Compute;
        EndCurrentPass();
        _endingFor = EncoderType.None;

        MTLComputeCommandEncoder computeCommandEncoder = _encoderFactory.CreateComputeCommandEncoder();

        ObjcOwnership.Retain(computeCommandEncoder.NativePtr);

        CurrentEncoder = computeCommandEncoder;
        CurrentEncoderType = EncoderType.Compute;
        return computeCommandEncoder;
    }
}
