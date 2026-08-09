using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Image;
using System;
using System.IO;
using System.Text;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    /// <summary>
    /// Per-draw fingerprints of the bound colour target, taken in the shared layer so
    /// both backends run this identical code over an identical command stream.
    ///
    /// Every instrument built for the white flash so far asked a question of one backend
    /// and needed a hypothesis to aim it - which mechanism, which value, which pass. Four
    /// candidate fixes died that way. This asks no question: it records what each draw
    /// produced, on Metal and on Vulkan, and the first draw index where the two disagree
    /// is the answer with no theory in front of it.
    ///
    /// The draw counter comes from the shared layer, so index N means the same guest draw
    /// on both sides by construction - the alignment problem that would sink a
    /// backend-side version of this does not exist here.
    ///
    /// RYUJINX_DRAW_TRACE=&lt;path&gt; writes the log. Readback is a full GPU sync per sampled
    /// draw, so this is a diagnostic build only, and the sampling interval keeps it to
    /// something that still reaches the reproduction: RYUJINX_DRAW_TRACE_EVERY (default
    /// 16) draws, over the frames between RYUJINX_DRAW_TRACE_FIRST and _LAST.
    /// </summary>
    static class DrawTrace
    {
        public static readonly bool Enabled =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RYUJINX_DRAW_TRACE"));

        private static readonly int _every = ReadInt("RYUJINX_DRAW_TRACE_EVERY", 16);
        private static readonly int _firstFrame = ReadInt("RYUJINX_DRAW_TRACE_FIRST", 0);
        private static readonly int _lastFrame = ReadInt("RYUJINX_DRAW_TRACE_LAST", int.MaxValue);

        private static int ReadInt(string name, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;
        }

        private static StreamWriter _writer;
        private static long _drawIndex;
        private static int _frame;

        public static void OnFrame()
        {
            _frame++;
            _drawIndex = 0;

            _writer?.Flush();
        }

        /// <summary>
        /// Fingerprints the bound colour target after a draw. Mean and a coarse hash over
        /// a fixed grid: the mean spots the white directly, and the hash separates "same
        /// brightness, different image" so a match cannot be a coincidence of averages.
        /// </summary>
        public static void AfterDraw(TextureManager textureManager)
        {
            if (!Enabled)
            {
                return;
            }

            long index = _drawIndex++;

            if (_frame < _firstFrame || _frame > _lastFrame || (index % _every) != 0)
            {
                return;
            }

            Image.Texture target = textureManager.GetAnyRenderTarget();

            if (target?.HostTexture == null || target.Info.Width < 256)
            {
                return;
            }

            try
            {
                ReadOnlySpan<byte> data = target.HostTexture.GetData().Get();

                if (data.Length < 4096)
                {
                    return;
                }

                // A fixed stride over the whole allocation rather than a rectangle: this
                // has to compare across backends whose row padding may differ, and a
                // stride touches the same fraction of every layout.
                long sum = 0;
                ulong hash = 1469598103934665603;
                int step = Math.Max(4, (data.Length / 4096) & ~3);

                for (int i = 0; i + 3 < data.Length; i += step)
                {
                    uint word = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));

                    sum += data[i] + data[i + 1] + data[i + 2];
                    hash = (hash ^ word) * 1099511628211;
                }

                int samples = Math.Max(1, data.Length / step);

                _writer ??= new StreamWriter(Environment.GetEnvironmentVariable("RYUJINX_DRAW_TRACE"), false)
                {
                    AutoFlush = false,
                };

                _writer.WriteLine(
                    $"f={_frame} d={index} {target.Info.Width}x{target.Info.Height} " +
                    $"{target.Info.FormatInfo.Format} mean={sum / (samples * 3.0):F1} h={hash:X16}");
            }
            catch (Exception exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"draw trace failed: {exception.Message}");
            }
        }
    }
}
