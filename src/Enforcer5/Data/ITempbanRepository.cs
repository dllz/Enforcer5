using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>One entry of the tempban schedule: the slot (Unix seconds it ends) and who it is for.</summary>
    internal sealed record TempbanEntry(string Slot, long ChatId, long UserId);

    /// <summary>
    /// The tempbans running in each chat and the schedule the expiry check works through. Written only by
    /// the TempBan, Ban and Unban actions (<c>Actions/ActionEffects</c>), the expiry check and a rejoin.
    /// </summary>
    internal interface ITempbanRepository
    {
        /// <summary>Tempbans in the chat that end after <paramref name="nowUtc"/>, one per user.</summary>
        Task<IReadOnlyList<TimedRestriction>> GetActiveAsync(long chatId, DateTime nowUtc);

        /// <summary>
        /// Schedules the end of a tempban at <paramref name="untilUtc"/>, or the first free second after it,
        /// and lists it in the chat. <paramref name="label"/> is <c>{name}:{group}</c>, for people reading
        /// the schedule. Returns the slot taken.
        /// </summary>
        Task<long> ScheduleAsync(long chatId, long userId, DateTime untilUtc, DateTime nowUtc, string label);

        /// <summary>
        /// Forgets every tempban of the user in the chat: its schedule entries, the chat's list and the
        /// expiry key. Cheap when there is none.
        /// </summary>
        Task CancelAsync(long chatId, long userId);

        /// <summary>Schedule entries that ended at or before <paramref name="nowUtc"/>.</summary>
        Task<IReadOnlyList<TempbanEntry>> DueAsync(DateTime nowUtc);

        /// <summary>Removes one entry from the schedule and the user from the chat's list.</summary>
        Task RemoveAsync(TempbanEntry entry);
    }
}
