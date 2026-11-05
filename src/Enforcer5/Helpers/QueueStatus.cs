using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// What one background queue is doing, for the watchdog and /queues (Daniel, 2026-11-05: the image queue hung on one
    /// download and nothing said so; a hang raises no error). A queue reports how many items wait, the item it started and
    /// when it finished one. <see cref="Check"/> finds a queue with items waiting and nothing finished for
    /// <see cref="StuckAfter"/>: the watchdog (<see cref="Start"/>) tells the error chat once per stall, and again when it
    /// moves.
    /// </summary>
    internal sealed class QueueStatus
    {
        internal static TimeSpan StuckAfter = TimeSpan.FromMinutes(2);
        internal static TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

        private static readonly ConcurrentDictionary<string, QueueStatus> All = new(StringComparer.Ordinal);

        private readonly object _lock = new();
        private int _waiting;
        private string _current;
        private DateTime? _startedAt;
        private DateTime _lastProgress = Clock.UtcNow;
        private long _done;
        private bool _reported;

        private QueueStatus(string name) => Name = name;

        internal string Name { get; }

        /// <summary>The status of the queue <paramref name="name"/>; the same object every time.</summary>
        internal static QueueStatus Register(string name) => All.GetOrAdd(name, n => new QueueStatus(n));

        internal static IReadOnlyList<QueueStatus> Queues => All.Values.OrderBy(q => q.Name, StringComparer.Ordinal).ToList();

        /// <summary>How many items wait (counting the one being worked on, if the queue counts it so).</summary>
        internal void Waiting(int count)
        {
            lock (_lock)
            {
                // Something new to do after being idle: the stall clock starts now, not at the last item done long ago.
                if (_waiting == 0 && count > 0 && _current == null) _lastProgress = Clock.UtcNow;
                _waiting = count;
            }
        }

        /// <summary>Work began on <paramref name="item"/> (a short description, no names).</summary>
        internal void Started(string item)
        {
            lock (_lock)
            {
                // Picked up after being idle: the stall clock starts now.
                if (_current == null && _waiting == 0) _lastProgress = Clock.UtcNow;
                _current = item;
                _startedAt = Clock.UtcNow;
            }
        }

        /// <summary>The item finished (whatever its outcome); <paramref name="waiting"/> are left.</summary>
        internal void Finished(int waiting)
        {
            lock (_lock)
            {
                _current = null;
                _startedAt = null;
                _lastProgress = Clock.UtcNow;
                _waiting = waiting;
                _done++;
            }
        }

        /// <summary>A snapshot for /queues and the watchdog.</summary>
        internal sealed record Snapshot(string Name, int Waiting, string Current, TimeSpan? Busy, TimeSpan SinceProgress, long Done)
        {
            internal bool Stuck => (Waiting > 0 || Current != null) && SinceProgress >= StuckAfter;

            public override string ToString()
            {
                var line = $"{Name}: {Waiting} waiting, {Done} done";
                if (Current != null) line += $", on {Current} for {Format(Busy ?? TimeSpan.Zero)}";
                if (Waiting > 0 || Current != null) line += $", last finished {Format(SinceProgress)} ago";
                return line;
            }
        }

        internal Snapshot Read()
        {
            var now = Clock.UtcNow;
            lock (_lock)
                return new Snapshot(Name, _waiting, _current, _startedAt is { } at ? now - at : null, now - _lastProgress, _done);
        }

        /// <summary>
        /// The queues stuck now that were not before, and those that moved again since; marks them so each is said once.
        /// Pure apart from that mark, for tests.
        /// </summary>
        internal static (IReadOnlyList<Snapshot> Stuck, IReadOnlyList<Snapshot> Moving) Check()
        {
            var stuck = new List<Snapshot>();
            var moving = new List<Snapshot>();
            foreach (var queue in All.Values)
            {
                var snapshot = queue.Read();
                lock (queue._lock)
                {
                    if (snapshot.Stuck && !queue._reported)
                    {
                        queue._reported = true;
                        stuck.Add(snapshot);
                    }
                    else if (!snapshot.Stuck && queue._reported)
                    {
                        queue._reported = false;
                        moving.Add(snapshot);
                    }
                }
            }
            return (stuck, moving);
        }

        /// <summary>The watchdog: every <see cref="CheckEvery"/>, a stuck queue goes to the error chat.</summary>
        internal static void Start(CancellationToken shutdown)
        {
            new Thread(() =>
            {
                while (!shutdown.IsCancellationRequested)
                {
                    try
                    {
                        Task.Delay(CheckEvery, shutdown).Wait(shutdown);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    try
                    {
                        Report();
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, "Checking the background queues");
                    }
                }
            }) { IsBackground = true, Name = "QueueWatchdog" }.Start();
        }

        /// <summary>One watchdog round: says what got stuck and what moved again. Called by the watchdog; tests call it directly.</summary>
        internal static void Report()
        {
            var (stuck, moving) = Check();
            foreach (var queue in stuck)
            {
                LogHelper.Info($"Queue stuck: {queue}");
                ErrorReporter.Report($"Queue stuck: {queue}. /queues shows them all; a restart clears it.");
            }
            foreach (var queue in moving)
            {
                LogHelper.Info($"Queue moving again: {queue}");
                ErrorReporter.Report($"Queue moving again: {queue}");
            }
        }

        private static string Format(TimeSpan span) =>
            span.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min {span.Seconds} s")
                : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds} s");

        internal static void ResetForTests()
        {
            foreach (var queue in All.Values)
                lock (queue._lock)
                {
                    queue._waiting = 0;
                    queue._current = null;
                    queue._startedAt = null;
                    queue._lastProgress = Clock.UtcNow;
                    queue._done = 0;
                    queue._reported = false;
                }
        }
    }
}
