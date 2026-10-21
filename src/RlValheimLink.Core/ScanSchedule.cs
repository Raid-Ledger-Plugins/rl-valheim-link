using System;

namespace RlValheimLink.Core
{
    /// <summary>
    /// The chest scan's fixed grid (docs/design-chests.md §1.7): the first slot <c>firstDelay</c> after
    /// the server half starts, then every <c>interval</c> measured from that first slot, not from each
    /// scan's end. A slot that finds a scan still running is skipped, and a slot missed while the server
    /// was stalled is not made up: the scan starts at the latest slot that has passed. The slot time is
    /// the scan's capturedAt, so snapshot gaps do not vary with how long decoding took.
    /// </summary>
    public sealed class ScanSchedule
    {
        private readonly TimeSpan _interval;

        public ScanSchedule(DateTime startUtc, TimeSpan firstDelay, TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            _interval = interval;
            NextAt = startUtc + firstDelay;
        }

        /// <summary>The next grid slot.</summary>
        public DateTime NextAt { get; private set; }

        public TimeSpan Interval => _interval;

        /// <summary>
        /// True when a scan should start now; <paramref name="scheduledAt"/> is its grid slot.
        /// <paramref name="skipped"/> counts slots passed without a scan (stalls, or a scan still running).
        /// </summary>
        public bool TryStart(DateTime nowUtc, bool running, out DateTime scheduledAt, out int skipped)
        {
            scheduledAt = default; skipped = 0;
            if (nowUtc < NextAt) return false;
            long behind = (nowUtc - NextAt).Ticks / _interval.Ticks;
            var slot = NextAt + TimeSpan.FromTicks(behind * _interval.Ticks);
            NextAt = slot + _interval;
            skipped = (int)Math.Min(int.MaxValue, behind);
            if (running) { skipped++; return false; }
            scheduledAt = slot;
            return true;
        }
    }
}
