using SharpMetal.Metal;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public struct RenderEncoderBindings
    {
        public List<Resource> Resources = [];
        public List<BufferResource> VertexBuffers = [];
        public List<BufferResource> FragmentBuffers = [];
        internal List<ScopedTemporaryBuffer> TemporaryBuffers = [];

        public RenderEncoderBindings() { }

        public readonly void Clear()
        {
            DisposeTemporaryBuffers();
            Resources.Clear();
            VertexBuffers.Clear();
            FragmentBuffers.Clear();
        }

        public readonly void DisposeTemporaryBuffers()
        {
            foreach (ScopedTemporaryBuffer buffer in TemporaryBuffers)
            {
                buffer.Dispose();
            }

            TemporaryBuffers.Clear();
        }
    }

    [SupportedOSPlatform("macos")]
    public struct ComputeEncoderBindings
    {
        public List<Resource> Resources = [];
        public List<BufferResource> Buffers = [];
        internal List<ScopedTemporaryBuffer> TemporaryBuffers = [];

        public ComputeEncoderBindings() { }

        public readonly void Clear()
        {
            DisposeTemporaryBuffers();
            Resources.Clear();
            Buffers.Clear();
        }

        public readonly void DisposeTemporaryBuffers()
        {
            foreach (ScopedTemporaryBuffer buffer in TemporaryBuffers)
            {
                buffer.Dispose();
            }

            TemporaryBuffers.Clear();
        }
    }

    public struct BufferResource
    {
        public MTLBuffer Buffer;
        public ulong Offset;
        public ulong Binding;

        public BufferResource(MTLBuffer buffer, ulong offset, ulong binding)
        {
            Buffer = buffer;
            Offset = offset;
            Binding = binding;
        }
    }

    public struct Resource
    {
        public MTLResource MtlResource;
        public MTLResourceUsage ResourceUsage;
        public MTLRenderStages Stages;

        public Resource(MTLResource resource, MTLResourceUsage resourceUsage, MTLRenderStages stages)
        {
            MtlResource = resource;
            ResourceUsage = resourceUsage;
            Stages = stages;
        }
    }
}
