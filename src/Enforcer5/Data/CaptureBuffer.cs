using System;
using System.Collections.Generic;
using System.Linq;
using Enforcer5.Helpers;

namespace Enforcer5.Data
{
    /// <summary>Messages someone sent in a group since the last flush. <see cref="RedisCount"/> is their whole count there.</summary>
    internal sealed record MemberActivity(long ChatId, long UserId, long Messages, DateTimeOffset FirstMessageAt,
        DateTimeOffset LastMessageAt, long RedisCount);

    /// <summary>Someone joined: how (<c>link</c>, <c>added</c>, <c>request</c>, <c>unknown</c>), through which link, added by whom.</summary>
    internal sealed record JoinEvent(long ChatId, long UserId, DateTimeOffset At, string Source, string InviteLink,
        string InviteLinkName, long? AddedBy);

    internal sealed record LeaveEvent(long ChatId, long UserId, DateTimeOffset At);

    internal sealed record FingerprintEvent(long ChatId, long UserId, long MessageId, DateTimeOffset At,
        MessageFingerprint Print, string Reason);

    internal sealed record CaptureBatch(IReadOnlyList<MemberActivity> Members, IReadOnlyList<JoinEvent> Joins,
        IReadOnlyList<LeaveEvent> Leaves, IReadOnlyList<FingerprintEvent> Fingerprints)
    {
        internal bool IsEmpty => Members.Count == 0 && Joins.Count == 0 && Leaves.Count == 0 && Fingerprints.Count == 0;
        internal static readonly CaptureBatch Empty = new(Array.Empty<MemberActivity>(), Array.Empty<JoinEvent>(),
            Array.Empty<LeaveEvent>(), Array.Empty<FingerprintEvent>());
    }

    /// <summary>
    /// Collects raid capture between flushes, like <see cref="ObservationBuffer"/> does for principals:
    /// thread-safe and bounded, so a raid or a PostgreSQL outage cannot grow it without limit.
    /// A join seen twice (the service message and the member update) within two minutes is one join,
    /// keeping whichever says more about how they came in.
    /// </summary>
    internal sealed class CaptureBuffer
    {
        private static readonly TimeSpan SameJoin = TimeSpan.FromMinutes(2);

        private readonly object _lock = new();
        private readonly int _max;
        private Dictionary<(long, long), MemberActivity> _members = new();
        private List<JoinEvent> _joins = new();
        private List<LeaveEvent> _leaves = new();
        private List<FingerprintEvent> _prints = new();
        private int _dropped;

        internal CaptureBuffer(int max = 50_000)
        {
            _max = max;
        }

        internal void Message(long chatId, long userId, DateTimeOffset at, long redisCount)
        {
            lock (_lock)
            {
                if (_members.TryGetValue((chatId, userId), out var m))
                    _members[(chatId, userId)] = m with
                    {
                        Messages = m.Messages + 1,
                        LastMessageAt = at > m.LastMessageAt ? at : m.LastMessageAt,
                        RedisCount = Math.Max(m.RedisCount, redisCount)
                    };
                else if (_members.Count < _max) _members[(chatId, userId)] = new MemberActivity(chatId, userId, 1, at, at, redisCount);
                else _dropped++;
            }
        }

        internal void Join(JoinEvent join)
        {
            lock (_lock)
            {
                var same = _joins.FindIndex(j => j.ChatId == join.ChatId && j.UserId == join.UserId &&
                                                 (j.At - join.At).Duration() <= SameJoin);
                if (same >= 0)
                {
                    var known = _joins[same];
                    if (known.Source == "unknown" || known.InviteLink == null && join.InviteLink != null)
                        _joins[same] = join with { At = known.At < join.At ? known.At : join.At, AddedBy = join.AddedBy ?? known.AddedBy };
                    return;
                }
                if (_joins.Count < _max) _joins.Add(join);
                else _dropped++;
            }
        }

        internal void Leave(LeaveEvent leave)
        {
            lock (_lock)
            {
                if (_leaves.Count < _max) _leaves.Add(leave);
                else _dropped++;
            }
        }

        internal void Fingerprint(FingerprintEvent print)
        {
            lock (_lock)
            {
                if (_prints.Count < _max) _prints.Add(print);
                else _dropped++;
            }
        }

        /// <summary>Everything collected, and how much was dropped for space; the buffer starts empty.</summary>
        internal (CaptureBatch Batch, int Dropped) Take()
        {
            lock (_lock)
            {
                var batch = new CaptureBatch(_members.Values.ToList(), _joins, _leaves, _prints);
                var dropped = _dropped;
                _members = new();
                _joins = new();
                _leaves = new();
                _prints = new();
                _dropped = 0;
                return (batch, dropped);
            }
        }

        /// <summary>Puts a batch that could not be written back, newer collections on top.</summary>
        internal void Restore(CaptureBatch batch)
        {
            lock (_lock)
            {
                foreach (var m in batch.Members)
                {
                    if (_members.TryGetValue((m.ChatId, m.UserId), out var newer))
                        _members[(m.ChatId, m.UserId)] = newer with
                        {
                            Messages = newer.Messages + m.Messages,
                            FirstMessageAt = m.FirstMessageAt < newer.FirstMessageAt ? m.FirstMessageAt : newer.FirstMessageAt
                        };
                    else if (_members.Count < _max) _members[(m.ChatId, m.UserId)] = m;
                }
                _joins.InsertRange(0, batch.Joins.Take(Math.Max(0, _max - _joins.Count)));
                _leaves.InsertRange(0, batch.Leaves.Take(Math.Max(0, _max - _leaves.Count)));
                _prints.InsertRange(0, batch.Fingerprints.Take(Math.Max(0, _max - _prints.Count)));
            }
        }
    }
}
