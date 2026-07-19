using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
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

                _handles[i] = device.NewLibrary(StringHelper.NSString(shader.Code), compileOptions, (library, error) => CompilationResultHandler(library, error, index));
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
