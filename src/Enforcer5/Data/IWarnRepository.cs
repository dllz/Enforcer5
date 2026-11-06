using System;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>What a change does to a member's warns of one kind.</summary>
    internal enum WarnOperation
    {
        /// <summary>Adds <see cref="WarnOp.Quantity"/>; the lifetime count grows with it.</summary>
        Add,
        /// <summary>Takes off up to <see cref="WarnOp.Quantity"/>, never below 0.</summary>
        Remove,
        /// <summary>Back to 0.</summary>
        Reset,
        /// <summary>To exactly <see cref="WarnOp.Quantity"/> (a data recovery); the lifetime count is left alone.</summary>
        Set
    }

    /// <summary>
    /// One change to a member's warns of one kind, as asked for. <see cref="Id"/> makes it idempotent: a change
    /// carried out twice (queued while PostgreSQL was down, then replayed) counts once. <see cref="ActorId"/> is the
    /// admin's Telegram id, null when Enforcer acted on its own; <see cref="ReportId"/> the report it came from.
    /// </summary>
    internal sealed record WarnOp(long ChatId, long UserId, WarnKind Kind, WarnOperation Operation, long Quantity)
    {
        public long? ActorId { get; init; }
        public string Reason { get; init; }
        public long? ReportId { get; init; }
        public string Id { get; init; }
        public DateTimeOffset At { get; init; }
        /// <summary>Order of changes queued while PostgreSQL was down.</summary>
        public long Sequence { get; init; }
    }

    /// <summary>
    /// What a change did: the count before and after, the lifetime count of that kind after, and the history event it
    /// wrote (null when nothing changed, or when it was queued and is not written yet).
    /// </summary>
    internal sealed record WarnChanged(long Before, long After, long Lifetime, long? EventId)
    {
        /// <summary>How many warns it added or took off.</summary>
        internal long Changed => Math.Abs(After - Before);

        /// <summary>Queued in Redis while PostgreSQL was down: the counts are the best estimate until it is written.</summary>
        public bool Queued { get; init; }
    }

    /// <summary>
    /// A member's warns in a group, for /user, /me and /getuser. <see cref="Lifetime"/> is as stored: it can be below
    /// <see cref="Warns"/> for a member Redis kept no lifetime count for.
    /// </summary>
    internal sealed record WarnCounts(long Warns, long Prewarns, long Media, long Lifetime);

    /// <summary>
    /// Members' warn counts per group (PostgreSQL rollout step 12: <c>warning_balances</c>, every change a
    /// <c>warning_events</c> row). Written by the warn actions (<c>Actions/WarnActions</c>), the warn removals and the
    /// data recovery; the count a change returns is what max warns and the media limit decide on.
    /// </summary>
    internal interface IWarnRepository
    {
        /// <summary>Current count, 0 if the member has none.</summary>
        Task<long> GetCountAsync(long chatId, WarnKind kind, long userId);

        /// <summary>All of a member's counts here.</summary>
        Task<WarnCounts> GetCountsAsync(long chatId, long userId);

        /// <summary>Makes the change and records it in the history, at once.</summary>
        Task<WarnChanged> ApplyAsync(WarnOp op);

        /// <summary>Sets the lifetime count of standard warns (a data recovery).</summary>
        Task SetLifetimeAsync(long chatId, long userId, long lifetime);
    }
}
