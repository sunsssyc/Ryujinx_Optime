using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Symptom mitigation for the TOTK white flash, not a fix for it.
    ///
    /// On this backend a fraction of frames reach Present with the source image a flat
    /// near-white - the scene is gone while the HUD still composites over it. The cause
    /// is unresolved (see docs/METAL_WHITE_FLASH_HANDOFF_2026-08-08.md); what is well
    /// established is that such a frame is uniform, which is something a rendered scene
    /// never is. This samples nine points spread across the source just before the
    /// present blit and, when they are all bright and all but identical, presents the
    /// other surface in the rotation - the previous frame's image - instead.
    ///
    /// Two costs, both real:
    /// - one GPU sync per frame, to have the samples on the CPU before deciding
    /// - a repeated frame whenever it fires, so motion stutters instead of flashing
    ///
    /// Off unless RYUJINX_METAL_FLASHGUARD=1. /tmp/ryujinx-metal-flashguard overrides it
    /// with 0 or 1, re-read once a frame, so both arms can be measured in one session.
    ///
    /// Known bad: with this on, a render encoder faulted inside the driver while its
    /// descriptor was being built (EXC_BAD_ACCESS in AGX FramebufferGen3, reached from
    /// renderCommandEncoderWithDescriptor), and the flash was still reported in play.
    /// The keep target this pass renders into is the only new attachment in the present
    /// path, so it is the first thing to suspect.
    ///
    /// Verified: 42.5% of sampled frames were flat white with it off, 0% with it on,
    /// judged from macOS compositor screenshots rather than the emulator's own probe,
    /// with the frame rate unchanged and the output confirmed to be ordinary gameplay.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class FlashGuard
    {
        private const int Pixels = 9;
        private const int BytesPerPixel = 4;

        // Same thresholds the detector was validated with: nine points spread over the
        // frame agreeing this closely is not a scene, even a blown-out sky.
        private const float UniformSpread = 2f;
        private const float WhiteLuma = 240f;

        private static bool _enabled;
        private static MTLBuffer _buf;

        private static Texture _previousSource;
        private static int _suppressed;
        private static int _seen;

        public static bool Enabled => _enabled;

        private static Texture _keep;
        private static int _keepWidth;
        private static int _keepHeight;

        /// <summary>
        /// A texture the size of the present source that holds the most recent frame that
        /// was not flat. Separate from the rotating present surfaces on purpose: those
        /// alternate every frame, so the "previous" one is flat as often as the current.
        /// </summary>
        public static Texture GetKeepTexture(MTLDevice device, MetalRenderer renderer, Pipeline pipeline, Texture like)
        {
            if (like == null)
            {
                return null;
            }

            if (_keep != null && _keepWidth == like.Width && _keepHeight == like.Height)
            {
                return _keep;
            }

            _keep?.Release();

            _keep = new Texture(device, renderer, pipeline, like.Info);
            _keepWidth = like.Width;
            _keepHeight = like.Height;

            return _keep;
        }

        public static void Init(MTLDevice device)
        {
            _buf = device.NewBuffer(Pixels * BytesPerPixel, MTLResourceOptions.ResourceStorageModeShared);
        }

        // Off by default. It measured clean in one scene, but in ordinary play the flash
        // was still reported and the render encoder faulted inside the driver while
        // building this pass's descriptor - so it is not fit to be on for anyone until
        // both are understood. RYUJINX_METAL_FLASHGUARD=1 opts in.
        private static readonly bool _defaultEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FLASHGUARD") == "1";

        public static void RefreshToggle()
        {
            try
            {
                if (System.IO.File.Exists("/tmp/ryujinx-metal-flashguard"))
                {
                    _enabled = System.IO.File.ReadAllText("/tmp/ryujinx-metal-flashguard").Trim() == "1";
                }
                else
                {
                    _enabled = _defaultEnabled;
                }
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        /// <summary>
        /// Samples the source. Returns the texture that should actually be presented:
        /// the previous frame's surface when this one is a flat white, otherwise the
        /// source unchanged.
        /// </summary>
        public static unsafe Texture Filter(CommandBufferScoped cbs, Texture src, Action flushAndWait)
        {
            if (!_enabled || _buf.NativePtr == IntPtr.Zero || src == null)
            {
                _previousSource = src;
                return src;
            }

            MTLTexture tex = src.GetHandle();

            if (tex.NativePtr == IntPtr.Zero)
            {
                return src;
            }

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            for (int i = 0; i < Pixels; i++)
            {
                blit.CopyFromTexture(
                    tex, 0, 0,
                    new MTLOrigin { x = (ulong)(src.Width * (i % 3 + 1) / 4), y = (ulong)(src.Height * (i / 3 + 1) / 4), z = 0 },
                    new MTLSize { width = 1, height = 1, depth = 1 },
                    _buf, (ulong)(i * BytesPerPixel), BytesPerPixel, BytesPerPixel);
            }

            // The samples have to be on the CPU before the present blit is chosen, so the
            // frame is committed and waited on here. This is the mitigation's main cost.
            flushAndWait();

            byte* p = (byte*)_buf.Contents;
            float min = 255f, max = 0f, total = 0f;

            for (int i = 0; i < Pixels; i++)
            {
                byte* px = p + i * BytesPerPixel;
                float luma = (px[0] + px[1] + px[1] + px[2]) * 0.25f;

                total += luma;
                min = MathF.Min(min, luma);
                max = MathF.Max(max, luma);
            }

            _seen++;

            bool flat = (max - min) <= UniformSpread && (total / Pixels) >= WhiteLuma;

            if (flat && _previousSource != null && !ReferenceEquals(_previousSource, src))
            {
                _suppressed++;

                if (_suppressed % 300 == 1)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"flashguard: repeated the previous frame {_suppressed} times out of {_seen}");
                }

                return _previousSource;
            }

            _previousSource = src;

            return src;
        }
    }
}
