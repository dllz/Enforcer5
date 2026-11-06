using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>One warn change from the history, newest first when listed.</summary>
    internal sealed record WarnRecord(long EventId, string WarningType, string Operation, long Quantity, string Reason,
        long? ActorId, DateTimeOffset OccurredAt);

    /// <summary>A member's balance of one warn type in one group.</summary>
    internal sealed record WarnBalance(string WarningType, long Active, long Lifetime);

    /// <summary>
    /// Reading the warn history and balances per group and member. They are written by <see cref="IWarnRepository"/>,
    /// with each change.
    /// </summary>
    internal interface IWarningHistoryRepository
    {
        /// <summary>The member's warn changes in this group, newest first.</summary>
        Task<IReadOnlyList<WarnRecord>> GetHistoryAsync(long chatId, long userId, int limit);

        /// <summary>The member's balances in this group.</summary>
        Task<IReadOnlyList<WarnBalance>> GetBalancesAsync(long chatId, long userId);
    }
}
