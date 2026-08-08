using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class Program : IProgram
    {
        private volatile ProgramLinkStatus _status;
        private readonly ShaderSource[] _shaders;
        private readonly GCHandle[] _handles;
        private readonly ManualResetEventSlim _compilationEvent = new(false);
        private int _successCount;

        private readonly MetalRenderer _renderer;

        public MTLFunction VertexFunction;
        public MTLFunction FragmentFunction;
        public MTLFunction ComputeFunction;
        public ComputeSize ComputeLocalSize { get; }

        private HashTableSlim<PipelineUid, MTLRenderPipelineState> _graphicsPipelineCache;
        private MTLComputePipelineState? _computePipelineCache;
        private bool _firstBackgroundUse;
        private string _debugLabel;
        private bool _sourcesDumped;

        /// <summary>
        /// Stable across runs: XXH3-128 of all stage sources, truncated to 16 hex
        /// chars. Used by the draw/dispatch trace to identify shaders, and as the
        /// file name prefix for <see cref="DumpSources"/>.
        /// </summary>
        public string DebugLabel
        {
            get
            {
                if (_debugLabel == null)
                {
                    int totalLength = 0;

                    foreach (ShaderSource shader in _shaders)
                    {
                        totalLength += Encoding.UTF8.GetByteCount(shader.Code) + 1;
                    }

                    byte[] combined = new byte[totalLength];
                    int position = 0;

                    foreach (ShaderSource shader in _shaders)
                    {
                        position += Encoding.UTF8.GetBytes(shader.Code, combined.AsSpan(position));
                        combined[position++] = 0;
                    }

                    _debugLabel = Hash128.ComputeHash(combined).ToString()[..16];
                }

                return _debugLabel;
            }
        }

        /// <summary>
        /// Writes the MSL source of every stage to <paramref name="directory"/> as
        /// {DebugLabel}-{stage}.metal, once per program instance. Diagnostic only.
        /// </summary>
        public void DumpSources(string directory)
        {
            if (_sourcesDumped)
            {
                return;
            }

            _sourcesDumped = true;

            try
            {
                Directory.CreateDirectory(directory);

                foreach (ShaderSource shader in _shaders)
                {
                    string path = Path.Combine(directory, $"{DebugLabel}-{shader.Stage.ToString().ToLowerInvariant()}.metal");

                    if (!File.Exists(path))
                    {
                        File.WriteAllText(path, shader.Code);
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Failed to dump shader sources for {DebugLabel}: {exception.Message}");
            }
        }

        /// <summary>
        /// Diagnostic: comma separated <see cref="DebugLabel"/> values whose fragment
        /// stage is compiled with every <c>discard_fragment()</c> removed. An alpha
        /// tested depth prepass that discards every fragment writes no depth at all,
        /// which makes the Equal tested shading pass that follows it draw nothing;
        /// dropping the discard tells that apart from the depth write itself failing.
        /// </summary>
        private static readonly string[] _noDiscardLabels =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_NO_DISCARD") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>
        /// Diagnostic: comma separated <see cref="DebugLabel"/> values whose fragment
        /// stage is compiled to write solid magenta to colour attachment 0, ignoring
        /// everything it computed. Geometry that is rasterised at all then shows up
        /// as a magenta silhouette, which separates "no fragments were produced" from
        /// "fragments were produced but their colour or depth went nowhere".
        /// </summary>
        private static readonly string[] _paintLabels =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_PAINT") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static readonly string[] _showBig =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_SHOW_BIG") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static readonly string[] _showNearZero =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_SHOW_NEARZERO") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>
        /// Greens a pixel when every one of the named values has reached 1, which for this
        /// shader is the whole of "it wrote white here": the frame goes flat when its three
        /// clamped products all saturate together.
        ///
        /// The point of asking it this way is that it needs no threshold. Three runs were
        /// spent on SHOW_BIG limits picked without knowing the value range, and each came
        /// back empty in a way that reads like a negative result but only says the limit
        /// was outside the data. Saturation is where the clamp is, so the question carries
        /// its own scale, and it is self-controlling: on an ordinary frame the few pixels
        /// that are genuinely blown out go green, and on a flat frame all of them do.
        ///
        /// RYUJINX_METAL_SHOW_SAT=&lt;label&gt;:temp_a,temp_b,temp_c
        /// </summary>
        private static readonly string[] _showSaturated =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_SHOW_SAT") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>
        /// Stamps a fixed magenta square in the corner of everything this shader writes.
        /// It answers one question and only that one: did this shader's pixels reach the
        /// screen on this frame?
        ///
        /// It exists because a marker that never fires and a value that never occurs look
        /// identical, and this file's notes are full of the second being read off the
        /// first. The saturation marker came back at 0.0% of pixels on flat frames - but
        /// also on ordinary ones, so on its own it says nothing. The stamp is
        /// unconditional, so it is present on any frame this shader painted, whatever the
        /// arithmetic did. A flat frame keeping the stamp and losing the green means the
        /// shader ran and did not saturate; a flat frame losing both means its output never
        /// reached the frame at all.
        ///
        /// RYUJINX_METAL_STAMP=&lt;label&gt;:&lt;pixels&gt;
        /// </summary>
        private static readonly string[] _stamp =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_STAMP") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static readonly string[] _guardBitTrickLabels =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_GUARD_BITTRICK") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static readonly string[] _guardDivideLabels =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_GUARD_DIVIDE") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static readonly string[] _clampFetchLabels =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_CLAMP_FETCH") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static readonly string[] _showInputLabels =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_SHOW_INPUT") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>
        /// A float literal MSL will accept. "G9" renders 100 as "100", and "100f" is not a
        /// valid literal - the shader fails to compile, its pipeline comes out null, the
        /// draw guard skips every draw that used it, and a full-screen pass silently
        /// leaves the frame black. That looked like the patch blanking the image.
        /// </summary>
        private static string MslFloat(float value)
        {
            string text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0)
            {
                text += ".0";
            }

            return text + "f";
        }

        private string PatchSourceForDiagnostics(ShaderSource shader)
        {
            if (shader.Stage != ShaderStage.Fragment)
            {
                return shader.Code;
            }

            string code = shader.Code;

            if (_noDiscardLabels.Length != 0 &&
                Array.IndexOf(_noDiscardLabels, DebugLabel) >= 0 &&
                code.Contains("discard_fragment();"))
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"diagnostic: compiling {DebugLabel} fragment without discard_fragment()");

                code = code.Replace("discard_fragment();", "{}");
            }

            if (_paintLabels.Length != 0 &&
                Array.IndexOf(_paintLabels, DebugLabel) >= 0 &&
                code.Contains("return out;"))
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"diagnostic: compiling {DebugLabel} fragment painting solid magenta");

                code = code.Replace("return out;", "out.color0 = float4(1.0f, 0.0f, 1.0f, 1.0f);\n    return out;");
            }

            // Diagnostic: green the pixels where a named value exceeds a magnitude,
            // leaving the rest of the frame alone. Marking the saturation itself rather
            // than a guess at which factor caused it - the frame goes flat when three
            // clamped products all reach 1, and either shared factor can do that.
            if (_showBig.Length == 3 && DebugLabel == _showBig[0] &&
                code.Contains("out.color0.w"))
            {
                string t = _showBig[1];

                if (code.Contains($"{t} =") &&
                    float.TryParse(_showBig[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float lim))
                {
                    int at = code.IndexOf("    out.color0.w", StringComparison.Ordinal);
                    int eol = code.IndexOf('\n', at);

                    if (at >= 0 && eol > at)
                    {
                        code = code.Insert(eol + 1,
                            $"    if (abs({t}) > {MslFloat(lim)}) {{ " +
                            "out.color0 = float4(0.0f, 1.0f, 0.0f, 1.0f); }\n");

                        Logger.Warning?.PrintMsg(LogClass.Gpu,
                            $"diagnostic: {DebugLabel} greens pixels where |{t}| > {lim}");
                    }
                }
            }

            if (_showSaturated.Length == 2 && DebugLabel == _showSaturated[0])
            {
                string[] temps = _showSaturated[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string[] missing = temps.Where(t => !code.Contains($"{t} =")).ToArray();
                int at = code.IndexOf("    out.color0.w", StringComparison.Ordinal);
                int eol = at >= 0 ? code.IndexOf('\n', at) : -1;

                // Say which of the two ways this can fail actually happened. A patch that
                // quietly applies to nothing, and is then read as "the value never occurs",
                // is the mistake these notes record most often.
                if (missing.Length != 0 || eol < 0)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"diagnostic: {DebugLabel} NOT marked - " +
                        (missing.Length != 0 ? $"absent: {string.Join(",", missing)}" : "no out.color0.w to insert after"));
                }
                else
                {
                    code = code.Insert(eol + 1,
                        $"    if ({string.Join(" && ", temps.Select(t => $"{t} >= 1.0f"))}) {{ " +
                        "out.color0 = float4(0.0f, 1.0f, 0.0f, 1.0f); }\n");

                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"diagnostic: {DebugLabel} greens pixels where {string.Join(" and ", temps)} all reach 1");
                }
            }

            if (_stamp.Length == 2 && DebugLabel == _stamp[0] &&
                int.TryParse(_stamp[1], out int stampSize) && stampSize > 0)
            {
                int at = code.IndexOf("    out.color0.w", StringComparison.Ordinal);
                int eol = at >= 0 ? code.IndexOf('\n', at) : -1;

                if (eol < 0)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"diagnostic: {DebugLabel} NOT stamped - no out.color0.w to insert after");
                }
                else
                {
                    // Placed after the last colour write, so it survives whatever the
                    // shader computed, and keyed on the fragment position so it lands in
                    // one corner rather than over the artefact being watched.
                    code = code.Insert(eol + 1,
                        $"    if (in.position.x < {MslFloat(stampSize)} && in.position.y < {MslFloat(stampSize)}) {{ " +
                        "out.color0 = float4(1.0f, 0.0f, 1.0f, 1.0f); }\n");

                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"diagnostic: {DebugLabel} stamps a {stampSize}x{stampSize} magenta corner");
                }
            }

            // Diagnostic: bound the bit-trick reciprocal. The guard below only matches
            // "1.0f / temp", and this shader's reciprocals are the 0x7EF07EBB bit trick
            // instead - so that guard never applied here and its negative result said
            // nothing. Dropping this shader's draws removes the flash entirely, so the
            // fault is inside it; this bounds the one construct in it that can produce a
            // non-finite value from a small input.
            if (_guardBitTrickLabels.Length != 0 &&
                Array.IndexOf(_guardBitTrickLabels, DebugLabel) >= 0)
            {
                MatchCollection sites = Regex.Matches(
                    code, @"(temp_\d+) = as_type<int>\(as_type<uint>\(temp_\d+\) \+ as_type<uint>\(as_type<int>\(0x7EF07EBB\)\)\);");

                int bounded = 0;

                foreach (Match site in sites)
                {
                    string name = site.Groups[1].Value;

                    string before = code;
                    code = code.Replace(
                        $"as_type<float>({name})",
                        $"clamp(as_type<float>({name}), -65504.0f, 65504.0f)");

                    if (!ReferenceEquals(before, code))
                    {
                        bounded++;
                    }
                }

                if (bounded != 0)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"diagnostic: bounded {bounded} bit-trick reciprocals in {DebugLabel}");
                }
            }

            // Diagnostic: make every reciprocal in this fragment finite. The guest
            // tonemap divides by a luminance it builds from the scene plus an unclamped
            // bloom term, then clamps the result to [0,1] - so a luminance near zero
            // sends every channel to 1.0 and the whole scene turns white. Bounding the
            // divisor tests whether that division is what produces the white frames.
            if (_guardDivideLabels.Length != 0 &&
                Array.IndexOf(_guardDivideLabels, DebugLabel) >= 0)
            {
                string guarded = Regex.Replace(
                    code,
                    @"= 1\.0f / (temp_\d+);",
                    m => $"= 1.0f / (abs({m.Groups[1].Value}) < 1e-6f ? (({m.Groups[1].Value}) < 0.0f ? -1e-6f : 1e-6f) : {m.Groups[1].Value});");

                if (guarded != code)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"diagnostic: compiling {DebugLabel} fragment with guarded reciprocals");
                    code = guarded;
                }
            }

            // Diagnostic: clamp every texture fetch this fragment makes to the same
            // ceiling the shader already applies to one of them. The guest tonemap
            // clamps its scene fetch to 10000 but adds an unclamped second fetch on top,
            // then divides by a luminance built from the sum - so an out-of-range value
            // arriving through the unclamped path saturates all three channels to 1.0.
            if (_clampFetchLabels.Length != 0 &&
                Array.IndexOf(_clampFetchLabels, DebugLabel) >= 0)
            {
                string clamped = Regex.Replace(
                    code,
                    @"(temp_\d+) = (textures\.[A-Za-z0-9_]+\.sample\([^;]*\)\.xyz);",
                    m => $"{m.Groups[1].Value} = min({m.Groups[2].Value}, float3(10000.0f));");

                if (clamped != code)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"diagnostic: compiling {DebugLabel} fragment with clamped texture fetches");
                    code = clamped;
                }
            }

            // Diagnostic: replace the tonemapped colour with a coarse magnitude scale of
            // the luminance it was given, so a screenshot reads off how large the input
            // actually is. The curve this shader implements asymptotes to 1.0, so a
            // blown-out input produces white legitimately - this separates "the shader
            // is wrong" from "the shader was handed an out-of-range image".
            if (_showInputLabels.Length != 0 &&
                Array.IndexOf(_showInputLabels, DebugLabel) >= 0 &&
                code.Contains("return out;"))
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"diagnostic: compiling {DebugLabel} fragment showing input magnitude");

                // Output the scene fetch itself. The shader's curve asymptotes to 1.0,
                // so it turns white either because it read white or because it read
                // something huge - showing the raw fetch tells those apart directly,
                // with no mid-frame readback to perturb the frame.
                // Show the sample coordinates instead of the sample. Skipping the draws
                // that fill this texture changes nothing and the output is uniform, which
                // is what happens when every pixel reads the same texel - so the question
                // is whether the interpolated coordinate collapses on those frames.
                code = code.Replace("return out;",
                    "out.color0 = float4(temp_0, temp_1, 0.0f, 1.0f);\n    return out;");
            }

            return code;
        }

        public ResourceBindingSegment[][] BindingSegments { get; }
        // Argument buffer sizes for Vertex or Compute stages
        public int[] ArgumentBufferSizes { get; }
        // Argument buffer sizes for Fragment stage
        public int[] FragArgumentBufferSizes { get; }

        public Program(
            MetalRenderer renderer,
            MTLDevice device,
            ShaderSource[] shaders,
            ResourceLayout resourceLayout,
            ComputeSize computeLocalSize = default)
        {
            _renderer = renderer;
            renderer.Programs.Add(this);

            ComputeLocalSize = computeLocalSize;
            _shaders = shaders;
            _handles = new GCHandle[_shaders.Length];

            _status = ProgramLinkStatus.Incomplete;

            for (int i = 0; i < _shaders.Length; i++)
            {
                ShaderSource shader = _shaders[i];

                using MTLCompileOptions compileOptions = new()
                {
                    PreserveInvariance = true,
                    LanguageVersion = MTLLanguageVersion.Version31,
                };
                int index = i;

                _handles[i] = device.NewLibrary(StringHelper.NSString(PatchSourceForDiagnostics(shader)), compileOptions, (library, error) => CompilationResultHandler(library, error, index));
            }

            (BindingSegments, ArgumentBufferSizes, FragArgumentBufferSizes) = BuildBindingSegments(resourceLayout.SetUsages);
        }

        public void CompilationResultHandler(MTLLibrary library, NSError error, int index)
        {
            ShaderSource shader = _shaders[index];

            if (_handles[index].IsAllocated)
            {
                _handles[index].Free();
            }

            if (error != IntPtr.Zero)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, shader.Code);
                Logger.Warning?.Print(LogClass.Gpu, $"{shader.Stage} shader linking failed: \n{StringHelper.String(error.LocalizedDescription)}");
                _status = ProgramLinkStatus.Failure;
                _compilationEvent.Set();
                return;
            }

            switch (shader.Stage)
            {
                case ShaderStage.Compute:
                    ComputeFunction = library.NewFunction(StringHelper.NSString("kernelMain"));
                    break;
                case ShaderStage.Vertex:
                    VertexFunction = library.NewFunction(StringHelper.NSString("vertexMain"));
                    break;
                case ShaderStage.Fragment:
                    FragmentFunction = library.NewFunction(StringHelper.NSString("fragmentMain"));
                    break;
                default:
                    Logger.Warning?.Print(LogClass.Gpu, $"Cannot handle stage {shader.Stage}!");
                    break;
            }

            if (Interlocked.Increment(ref _successCount) >= _shaders.Length && _status != ProgramLinkStatus.Failure)
            {
                _status = ProgramLinkStatus.Success;
                _compilationEvent.Set();
            }
        }

        private static (ResourceBindingSegment[][], int[], int[]) BuildBindingSegments(ReadOnlyCollection<ResourceUsageCollection> setUsages)
        {
            ResourceBindingSegment[][] segments = new ResourceBindingSegment[setUsages.Count][];
            int[] argBufferSizes = new int[setUsages.Count];
            int[] fragArgBufferSizes = new int[setUsages.Count];

            for (int setIndex = 0; setIndex < setUsages.Count; setIndex++)
            {
                List<ResourceBindingSegment> currentSegments = [];

                ResourceUsage currentUsage = default;
                int currentCount = 0;

                for (int index = 0; index < setUsages[setIndex].Usages.Count; index++)
                {
                    ResourceUsage usage = setUsages[setIndex].Usages[index];

                    if (currentUsage.Binding + currentCount != usage.Binding ||
                        currentUsage.Type != usage.Type ||
                        currentUsage.Stages != usage.Stages ||
                        currentUsage.ArrayLength > 1 ||
                        usage.ArrayLength > 1)
                    {
                        if (currentCount != 0)
                        {
                            currentSegments.Add(new ResourceBindingSegment(
                                currentUsage.Binding,
                                currentCount,
                                currentUsage.Type,
                                currentUsage.Stages,
                                currentUsage.ArrayLength > 1));

                            int size = currentCount * ResourcePointerSize(currentUsage.Type);
                            if (currentUsage.Stages.HasFlag(ResourceStages.Fragment))
                            {
                                fragArgBufferSizes[setIndex] += size;
                            }

                            if (currentUsage.Stages.HasFlag(ResourceStages.Vertex) ||
                                currentUsage.Stages.HasFlag(ResourceStages.Compute))
                            {
                                argBufferSizes[setIndex] += size;
                            }
                        }

                        currentUsage = usage;
                        currentCount = usage.ArrayLength;
                    }
                    else
                    {
                        currentCount++;
                    }
                }

                if (currentCount != 0)
                {
                    currentSegments.Add(new ResourceBindingSegment(
                        currentUsage.Binding,
                        currentCount,
                        currentUsage.Type,
                        currentUsage.Stages,
                        currentUsage.ArrayLength > 1));

                    int size = currentCount * ResourcePointerSize(currentUsage.Type);
                    if (currentUsage.Stages.HasFlag(ResourceStages.Fragment))
                    {
                        fragArgBufferSizes[setIndex] += size;
                    }

                    if (currentUsage.Stages.HasFlag(ResourceStages.Vertex) ||
                        currentUsage.Stages.HasFlag(ResourceStages.Compute))
                    {
                        argBufferSizes[setIndex] += size;
                    }
                }

                segments[setIndex] = currentSegments.ToArray();
            }

            return (segments, argBufferSizes, fragArgBufferSizes);
        }

        private static int ResourcePointerSize(ResourceType type)
        {
            return (type == ResourceType.TextureAndSampler ? 2 : 1);
        }

        public ProgramLinkStatus CheckProgramLink(bool blocking)
        {
            if (blocking)
            {
                _compilationEvent.Wait();
            }

            return _status;
        }

        public byte[] GetBinary()
        {
            return MslProgramBinarySerializer.Pack(_shaders);
        }

        public void AddGraphicsPipeline(ref PipelineUid key, MTLRenderPipelineState pipeline)
        {
            (_graphicsPipelineCache ??= new()).Add(ref key, pipeline);
        }

        public void AddComputePipeline(MTLComputePipelineState pipeline)
        {
            _computePipelineCache = pipeline;
        }

        public bool TryGetGraphicsPipeline(ref PipelineUid key, out MTLRenderPipelineState pipeline)
        {
            if (_graphicsPipelineCache == null)
            {
                pipeline = default;
                return false;
            }

            if (!_graphicsPipelineCache.TryGetValue(ref key, out pipeline))
            {
                if (_firstBackgroundUse)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Background pipeline compile missed on draw - incorrect pipeline state?");
                    _firstBackgroundUse = false;
                }

                return false;
            }

            _firstBackgroundUse = false;

            return true;
        }

        public bool TryGetComputePipeline(out MTLComputePipelineState pipeline)
        {
            if (_computePipelineCache.HasValue)
            {
                pipeline = _computePipelineCache.Value;
                return true;
            }

            pipeline = default;
            return false;
        }

        public void Dispose()
        {
            if (!_renderer.Programs.Remove(this))
            {
                return;
            }

            if (_graphicsPipelineCache != null)
            {
                foreach (MTLRenderPipelineState pipeline in _graphicsPipelineCache.Values)
                {
                    pipeline.Dispose();
                }
            }

            _computePipelineCache?.Dispose();

            VertexFunction.Dispose();
            FragmentFunction.Dispose();
            ComputeFunction.Dispose();
            _compilationEvent.Dispose();
        }
    }
}
