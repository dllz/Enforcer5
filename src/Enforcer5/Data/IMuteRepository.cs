using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>
    /// The bot's record of who it muted in each chat. Telegram enforces the mute itself, including
    /// lifting a tempmute when it ends; this record only backs /mutelist, /listmutedjoiners and
    /// /unmutenewjoiners. A user is in at most one of the three lists: the latest mute wins.
    /// </summary>
    internal interface IMuteRepository
    {
        /// <summary>Records a /mute with no end.</summary>
        Task MarkMutedAsync(long chatId, long userId);

        /// <summary>Records a /tempmute ending at <paramref name="untilUtc"/>.</summary>
        Task MarkTempMutedAsync(long chatId, long userId, DateTime untilUtc);

        /// <summary>
        /// Records a mute applied by Mute On Join. A user an admin already muted for good stays in
        /// that list, so /unmutenewjoiners does not unmute them.
        /// </summary>
        Task MarkJoinerMutedAsync(long chatId, long userId);

        /// <summary>Forgets every mute of the user in the chat.</summary>
        Task ClearAsync(long chatId, long userId);

        Task<IReadOnlyList<long>> GetMutedAsync(long chatId);

        Task<IReadOnlyList<long>> GetMutedJoinersAsync(long chatId);

        /// <summary>Tempmutes still running at <paramref name="nowUtc"/>. Ended ones are removed.</summary>
        Task<IReadOnlyList<TimedRestriction>> GetTempMutedAsync(long chatId, DateTime nowUtc);
    }
}
