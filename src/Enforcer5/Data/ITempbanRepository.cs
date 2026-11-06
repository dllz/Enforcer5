using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>One entry of the tempban schedule: the slot (Unix seconds it ends) and who it is for.</summary>
    internal sealed record TempbanEntry(string Slot, long ChatId, long UserId);

    /// <summary>A tempban of one user: the chat, and when it ends (UTC).</summary>
    internal sealed record UserTempban(long ChatId, DateTime UntilUtc);

    /// <summary>
    /// The tempbans running in each chat and the schedule the expiry check works through, per edition (PostgreSQL
    /// since rollout step 13). Written only by the TempBan, Ban and Unban actions (<c>Actions/ActionEffects</c>), the
    /// expiry check and a rejoin.
    /// </summary>
    internal interface ITempbanRepository
    {
        /// <summary>Tempbans in the chat that end after <paramref name="nowUtc"/>, one per user.</summary>
        Task<IReadOnlyList<TimedRestriction>> GetActiveAsync(long chatId, DateTime nowUtc);

        /// <summary>
        /// Schedules the end of the member's tempban at <paramref name="untilUtc"/> (to the second), replacing one
        /// scheduled before. Returns the end as Unix seconds.
        /// </summary>
        Task<long> ScheduleAsync(long chatId, long userId, DateTime untilUtc, DateTime nowUtc);

        /// <summary>Forgets the member's tempban in the chat. Cheap when there is none.</summary>
        Task CancelAsync(long chatId, long userId);

        /// <summary>The user's tempbans that end after <paramref name="nowUtc"/>, one per chat, soonest first.</summary>
        Task<IReadOnlyList<UserTempban>> OfUserAsync(long userId, DateTime nowUtc);

        /// <summary>
        /// Schedule entries that ended at or before <paramref name="nowUtc"/>, claimed for this process until
        /// <see cref="RemoveAsync"/> (or for 10 minutes).
        /// </summary>
        Task<IReadOnlyList<TempbanEntry>> DueAsync(DateTime nowUtc);

        /// <summary>Removes one entry, unless the member was tempbanned again since.</summary>
        Task RemoveAsync(TempbanEntry entry);
    }
}
