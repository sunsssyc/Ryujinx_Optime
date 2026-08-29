using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Host GPU occupancy, taken from each command buffer's own GPUStartTime/GPUEndTime.
    ///
    /// This answers the one question the whole performance argument turns on: when the
    /// game drops to sixteen frames a second, is the GPU saturated or is it starving?
    /// Every earlier attribution here ("host GPU ~27% busy, backend thread ~70% idle")
    /// was measured in a quiet segment and predates the buffer mirror; the scenes that
    /// actually stutter run 6.5x the draws, so that number does not transfer.
    ///
    /// Intervals are unioned, not summed. Command buffers from different threads overlap
    /// on the GPU and summing them reports occupancy above 100%. The sum is kept as well,
    /// because sum/union names how much overlap there was.
    ///
    /// The timestamps are CFTimeInterval seconds on the mach_absolute_time base, which is
    /// also what Stopwatch reads here - but that is an assumption, not something the API
    /// promises, so the snapshot reports the span from the first start to the last end.
    /// If the clocks agree the span matches the window's wall time. A span that does not
    /// means the reading is wrong, not that the GPU behaved strangely.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class GpuTimeline
    {
        private const int Capacity = 32768;
        private const double GapThresholdSeconds = 0.001;

        private static readonly object _lock = new();
        private static readonly double[] _starts = new double[Capacity];
        private static readonly double[] _ends = new double[Capacity];
        private static readonly double[] _scratchStarts = new double[Capacity];
        private static readonly double[] _scratchEnds = new double[Capacity];

        private static int _count;
        private static int _dropped;

        /// <summary>
        /// On by default - reading two doubles off a command buffer that is already known
        /// complete costs nothing. RYUJINX_METAL_GPU_TIME=0 removes even that.
        /// </summary>
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_GPU_TIME") != "0";

        public readonly struct Snapshot
        {
            public int Count { get; init; }
            public int Dropped { get; init; }

            /// <summary>Union of the execution intervals - the time the GPU was doing something.</summary>
            public double BusySeconds { get; init; }

            /// <summary>Sum of the intervals. Over the union by however much they overlapped.</summary>
            public double SumSeconds { get; init; }

            /// <summary>First start to last end. Compare against the window's wall time to check the clock base.</summary>
            public double SpanSeconds { get; init; }

            /// <summary>Span minus busy - the GPU sat with nothing to run for this long.</summary>
            public double IdleSeconds => Math.Max(0.0, SpanSeconds - BusySeconds);

            public double LongestGapSeconds { get; init; }
            public int GapsOverThreshold { get; init; }
        }

        /// <summary>
        /// Records one completed command buffer. Zero timestamps mean the buffer never
        /// reached the GPU (an error, or nothing to run) and are dropped rather than
        /// counted as an interval starting at the epoch.
        /// </summary>
        public static void Note(double start, double end)
        {
            if (start <= 0.0 || end <= start)
            {
                return;
            }

            lock (_lock)
            {
                if (_count == Capacity)
                {
                    _dropped++;
                    return;
                }

                _starts[_count] = start;
                _ends[_count] = end;
                _count++;
            }
        }

        public static Snapshot TakeAndReset()
        {
            int n;
            int dropped;

            lock (_lock)
            {
                n = _count;
                dropped = _dropped;

                Array.Copy(_starts, _scratchStarts, n);
                Array.Copy(_ends, _scratchEnds, n);

                _count = 0;
                _dropped = 0;
            }

            if (n == 0)
            {
                return new Snapshot { Count = 0, Dropped = dropped };
            }

            // Sorting the ends along with the starts keeps each interval's pair together.
            Array.Sort(_scratchStarts, _scratchEnds, 0, n);

            double busy = 0.0;
            double sum = 0.0;
            double longestGap = 0.0;
            int gaps = 0;

            double runStart = _scratchStarts[0];
            double runEnd = _scratchEnds[0];
            double maxEnd = runEnd;

            sum += runEnd - runStart;

            for (int i = 1; i < n; i++)
            {
                double s = _scratchStarts[i];
                double e = _scratchEnds[i];

                sum += e - s;

                if (e > maxEnd)
                {
                    maxEnd = e;
                }

                if (s > runEnd)
                {
                    busy += runEnd - runStart;

                    double gap = s - runEnd;

                    if (gap > longestGap)
                    {
                        longestGap = gap;
                    }

                    if (gap > GapThresholdSeconds)
                    {
                        gaps++;
                    }

                    runStart = s;
                    runEnd = e;
                }
                else if (e > runEnd)
                {
                    runEnd = e;
                }
            }

            busy += runEnd - runStart;

            return new Snapshot
            {
                Count = n,
                Dropped = dropped,
                BusySeconds = busy,
                SumSeconds = sum,
                SpanSeconds = maxEnd - _scratchStarts[0],
                LongestGapSeconds = longestGap,
                GapsOverThreshold = gaps,
            };
        }
    }
}
