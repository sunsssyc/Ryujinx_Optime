using System.Threading;

namespace Ryujinx.Common
{
    /// <summary>
    /// Static per-frame counters for diagnosing SynchronizeMemory behavior.
    /// Incremented by TextureGroup, read/reset by PresentProbe.
    /// </summary>
    public static class SyncMemDiag
    {
        /// <summary>Count of handles where dirty=true but Modified=false → CPU data uploaded over GPU result.</summary>
        private static int _uploads;

        /// <summary>Count of handles where dirty=true and Modified=true → upload suppressed by GPU ownership.</summary>
        private static int _protected;

        /// <summary>Count of handles where dirty=false → no action needed.</summary>
        private static int _clean;

        /// <summary>Count of FlushAction calls (each call clears Modified on one handle).</summary>
        private static int _flushActions;

        public static void IncrementUpload() => Interlocked.Increment(ref _uploads);
        public static void IncrementProtected() => Interlocked.Increment(ref _protected);
        public static void IncrementClean() => Interlocked.Increment(ref _clean);
        public static void IncrementFlushAction() => Interlocked.Increment(ref _flushActions);

        /// <summary>
        /// Snapshot and reset all counters atomically (per-counter).
        /// Call once per present from PresentProbe.
        /// </summary>
        public static (int uploads, int protectedCount, int clean, int flushActions) SnapshotAndReset()
        {
            int u = Interlocked.Exchange(ref _uploads, 0);
            int p = Interlocked.Exchange(ref _protected, 0);
            int c = Interlocked.Exchange(ref _clean, 0);
            int f = Interlocked.Exchange(ref _flushActions, 0);
            return (u, p, c, f);
        }
    }
}
