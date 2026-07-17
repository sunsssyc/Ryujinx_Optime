using SharpMetal;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal.SharpMetalExtensions
{
    [SupportedOSPlatform("macOS")]
    public static class MTLCommandEncoderExtensions
    {
        private static readonly Selector SelUseRenderResources = "useResources:count:usage:stages:";
        private static readonly Selector SelUseComputeResources = "useResources:count:usage:";

        public static unsafe void UseResourcesCompat(
            this MTLRenderCommandEncoder encoder,
            MTLResource[] resources,
            ulong count,
            MTLResourceUsage usage,
            MTLRenderStages stages)
        {
            fixed (MTLResource* resourcesPtr = resources)
            {
                ObjectiveCRuntime.objc_msgSend(
                    encoder.NativePtr,
                    SelUseRenderResources,
                    (IntPtr)resourcesPtr,
                    count,
                    (ulong)usage,
                    (ulong)stages);
            }
        }

        public static unsafe void UseResourcesCompat(
            this MTLComputeCommandEncoder encoder,
            MTLResource[] resources,
            ulong count,
            MTLResourceUsage usage)
        {
            fixed (MTLResource* resourcesPtr = resources)
            {
                ObjectiveCRuntime.objc_msgSend(
                    encoder.NativePtr,
                    SelUseComputeResources,
                    (IntPtr)resourcesPtr,
                    count,
                    (ulong)usage);
            }
        }
    }
}
