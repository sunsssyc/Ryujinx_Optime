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
                    CurrentEncoder = null;
                    break;
                case EncoderType.Compute:
                    ComputeEncoder.EndEncoding();
                    CurrentEncoder = null;
                    break;
                case EncoderType.Render:
                    RenderEncoder.EndEncoding();
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

        CurrentEncoder = computeCommandEncoder;
        CurrentEncoderType = EncoderType.Compute;
        return computeCommandEncoder;
    }
}
