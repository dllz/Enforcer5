using System;
using System.Collections.Generic;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Decides which errors are sent to the error chat. Every error is still written to the log;
    /// this only keeps the chat readable. The same error (same key) is sent at most once per
    /// window, and at most PerMinute reports go out in any minute, so a Telegram or Redis outage
    /// that fails every update cannot flood the chat or earn the bot a 429 there.
    ///
    /// Repeats are counted, not lost: the next report for a key says how many were held back,
    /// and the next report of any kind says how many the per-minute cap dropped.
    /// </summary>
    internal sealed class ErrorThrottle
    {
        internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);
        internal const int DefaultPerMinute = 20;
        private const int MaxKeys = 1000;

        private readonly Func<DateTime> _clock;
        private readonly TimeSpan _window;
        private readonly int _perMinute;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly Queue<DateTime> _sent = new Queue<DateTime>();
        private int _dropped;

        private sealed class Entry
        {
            public DateTime LastSent;
            public int Suppressed;
        }

        internal ErrorThrottle(Func<DateTime> clock, TimeSpan? window = null, int perMinute = DefaultPerMinute)
        {
            _clock = clock;
            _window = window ?? DefaultWindow;
            _perMinute = perMinute;
        }

        /// <summary>
        /// True when this error should be sent now. <paramref name="suppressed"/> is how many of
        /// this key were held back since it was last sent; <paramref name="dropped"/> how many
        /// errors of any key the per-minute cap dropped since the last report went out.
        /// </summary>
        internal bool TryAcquire(string key, out int suppressed, out int dropped)
        {
            suppressed = 0;
            dropped = 0;
            var now = _clock();
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var entry) && now - entry.LastSent < _window)
                {
                    entry.Suppressed++;
                    return false;
                }

                while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromMinutes(1))
                    _sent.Dequeue();
                if (_sent.Count >= _perMinute)
                {
                    _dropped++;
                    return false;
                }

                if (entry == null)
                {
                    if (_entries.Count >= MaxKeys)
                        Prune(now);
                    entry = new Entry();
                    _entries[key] = entry;
                }

                suppressed = entry.Suppressed;
                dropped = _dropped;
                entry.Suppressed = 0;
                entry.LastSent = now;
                _dropped = 0;
                _sent.Enqueue(now);
                return true;
            }
        }

        private void Prune(DateTime now)
        {
            var stale = new List<string>();
            foreach (var pair in _entries)
                if (now - pair.Value.LastSent >= _window)
                    stale.Add(pair.Key);
            foreach (var key in stale)
                _entries.Remove(key);
            // Everything is recent: forget it all rather than grow without bound.
            if (_entries.Count >= MaxKeys)
                _entries.Clear();
        }
    }
}
