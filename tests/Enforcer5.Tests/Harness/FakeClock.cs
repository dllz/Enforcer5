using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// A simulated clock for <see cref="Enforcer5.Helpers.Clock"/>. Time only moves when a test
    /// moves it, and timers fire synchronously, in due order, inside <see cref="AdvanceTo"/>. That
    /// lets a scenario run a 30-minute raid in milliseconds.
    /// </summary>
    internal sealed class FakeClock : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<FakeTimer> _timers = new();
        private DateTimeOffset _now;
        private long _sequence;

        internal FakeClock(DateTimeOffset start)
        {
            _now = start;
        }

        /// <summary>Starts at the current real time, whole seconds, so older code that still reads
        /// DateTime.UtcNow sees roughly the same day.</summary>
        internal static FakeClock StartingNow()
        {
            var now = DateTimeOffset.UtcNow;
            return new FakeClock(new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero));
        }

        internal DateTime UtcNow => GetUtcNow().UtcDateTime;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock) return _now;
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_lock) return _now.UtcTicks;
        }

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>When the next timer fires, or null when none is waiting.</summary>
        internal DateTimeOffset? NextDue
        {
            get
            {
                lock (_lock) return _timers.Count == 0 ? null : _timers.Min(t => t.Due);
            }
        }

        /// <summary>Moves time on by <paramref name="by"/>, firing every timer due on the way.</summary>
        internal void Advance(TimeSpan by) => AdvanceTo(GetUtcNow() + by);

        /// <summary>
        /// Moves time to <paramref name="target"/>, firing every timer due on the way, in order, each
        /// with the clock set to its own due time. Never moves time backwards.
        /// </summary>
        internal void AdvanceTo(DateTimeOffset target)
        {
            while (true)
            {
                FakeTimer next;
                lock (_lock)
                {
                    next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).ThenBy(t => t.Sequence).FirstOrDefault();
                    if (next == null)
                    {
                        if (target > _now) _now = target;
                        return;
                    }
                    if (next.Due > _now) _now = next.Due;
                    _timers.Remove(next);
                    if (next.Period > TimeSpan.Zero)
                    {
                        next.Due += next.Period;
                        next.Sequence = ++_sequence;
                        _timers.Add(next);
                    }
                }
                next.Fire();
            }
        }

        private void Schedule(FakeTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
                if (dueTime == Timeout.InfiniteTimeSpan) return;
                timer.Due = _now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                timer.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                timer.Sequence = ++_sequence;
                _timers.Add(timer);
            }
        }

        private void Remove(FakeTimer timer)
        {
            lock (_lock) _timers.Remove(timer);
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly FakeClock _clock;
            private readonly TimerCallback _callback;
            private readonly object _state;

            internal FakeTimer(FakeClock clock, TimerCallback callback, object state)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
            }

            internal DateTimeOffset Due;
            internal TimeSpan Period;
            internal long Sequence;

            internal void Fire() => _callback(_state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _clock.Schedule(this, dueTime, period);
                return true;
            }

            public void Dispose() => _clock.Remove(this);

            public System.Threading.Tasks.ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
