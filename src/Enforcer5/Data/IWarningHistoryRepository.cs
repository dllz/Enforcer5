using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>What happened to a warn balance. <c>decay</c> is in the schema; nothing makes it yet.</summary>
    internal enum WarnOperation
    {
        Add,
        Remove,
        Reset
    }

    /// <summary>
    /// One change to a member's warns, as Redis has already applied it.
    /// <paramref name="Quantity"/> is how many warns were added or taken off (positive).
    /// <paramref name="ActiveAfter"/> is the count Redis holds now; the balance copies it rather than
    /// adding to what PostgreSQL had, so it matches Redis from the first change on.
    /// <paramref name="LifetimeAfter"/> is Redis's <c>totalWarns</c> where Redis keeps one (standard
    /// warns from /warn); when null the lifetime count adds <paramref name="Quantity"/> on an add.
    /// <paramref name="ActorId"/> is the admin's Telegram id, null when Enforcer acted on its own.
    /// <paramref name="ReportId"/> is the report the warn was given from.
    /// </summary>
    internal sealed record WarnEvent(long ChatId, long UserId, WarnKind Kind, WarnOperation Operation, long Quantity,
        long ActiveAfter, long? LifetimeAfter, string Reason, long? ActorId, DateTimeOffset At, long? ReportId = null);

    /// <summary>One warn change from the history, newest first when listed.</summary>
    internal sealed record WarnRecord(long EventId, string WarningType, string Operation, long Quantity, string Reason,
        long? ActorId, DateTimeOffset OccurredAt);

    /// <summary>A member's balance of one warn type in one group.</summary>
    internal sealed record WarnBalance(string WarningType, long Active, long Lifetime);

    /// <summary>
    /// Warn history and balances per group and member. Writes only: Redis stays what the bot reads
    /// (see <see cref="Helpers.WarnHistory"/>).
    /// </summary>
    internal interface IWarningHistoryRepository
    {
        /// <summary>Writes the event and the balance it leaves, in one transaction. Returns the event id.</summary>
        Task<long> RecordAsync(WarnEvent warn);

        /// <summary>The member's warn changes in this group, newest first.</summary>
        Task<IReadOnlyList<WarnRecord>> GetHistoryAsync(long chatId, long userId, int limit);

        /// <summary>The member's balances in this group.</summary>
        Task<IReadOnlyList<WarnBalance>> GetBalancesAsync(long chatId, long userId);
    }
}
