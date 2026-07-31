using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    readonly struct DisposableTexture : IDisposable
    {
        public MTLTexture Value { get; }

        public DisposableTexture(MTLTexture texture)
        {
            Value = texture;
        }

        public void Dispose()
        {
            if (Value != IntPtr.Zero)
            {
                Value.Dispose();
            }
        }
    }
}
