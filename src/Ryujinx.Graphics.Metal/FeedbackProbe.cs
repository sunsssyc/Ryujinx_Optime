using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Counts draws that sample a texture which is simultaneously one of their own colour
    /// attachments. Metal leaves that undefined, and undefined reads on this hardware
    /// return near-white - the flash's exact value - while no write occurs, which is why
    /// every write-side hook in these notes came back empty.
    ///
    /// Reports distinct (attachment index, size, format) combinations once each, plus a
    /// running total, so a real occurrence is unmistakable and a clean run is a genuine
    /// negative rather than a silent one.
    ///
    /// RYUJINX_METAL_FEEDBACK=1.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class FeedbackProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FEEDBACK") == "1";

        private static readonly HashSet<string> _seen = [];
        private static long _total;

        public static void Note(int attachmentIndex, int width, int height, string format, IntPtr root)
        {
            _total++;

            string key = $"{attachmentIndex}:{width}x{height}:{format}";

            lock (_seen)
            {
                if (!_seen.Add(key) && (_total % 5000) != 0)
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"FEEDBACK: sampling attachment {attachmentIndex} {width}x{height} {format} " +
                $"root=0x{root:X} (total {_total})");
        }
    }
}
