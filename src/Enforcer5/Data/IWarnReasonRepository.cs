using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>
    /// Everything for warn removal reasons: the per-group setting, the saved and recent reasons,
    /// and the removals waiting for a reason.
    /// </summary>
    internal interface IWarnReasonRepository
    {
        /// <summary>The group's setting; <see cref="WarnReasonMode.Off"/> if never set.</summary>
        Task<WarnReasonMode> GetModeAsync(long chatId);

        Task SetModeAsync(long chatId, WarnReasonMode mode);

        /// <summary>
        /// The group's saved reasons, or null if admins have never edited the list, in which case
        /// the language file's defaults apply. An edited list that is now empty is returned empty.
        /// </summary>
        Task<IReadOnlyList<string>> GetSavedAsync(long chatId);

        /// <summary>Replaces the saved reasons. From then on the defaults no longer apply.</summary>
        Task SetSavedAsync(long chatId, IReadOnlyList<string> reasons);

        /// <summary>Recently used reasons, newest first, at most <see cref="WarnReasons.MaxRecent"/>.</summary>
        Task<IReadOnlyList<string>> GetRecentAsync(long chatId);

        /// <summary>Moves <paramref name="reason"/> to the front of the recent list.</summary>
        Task AddRecentAsync(long chatId, string reason);

        /// <summary>Stores a removal waiting for its reason, keyed by the question's message id.</summary>
        Task SavePendingAsync(long chatId, int promptMessageId, PendingWarnRemoval pending, TimeSpan lifetime);

        /// <summary>The waiting removal, or null if there is none or it expired.</summary>
        Task<PendingWarnRemoval> GetPendingAsync(long chatId, int promptMessageId);

        /// <summary>
        /// Removes the waiting entry. True for exactly one caller, so when two answers race only
        /// the one that claims it applies the removal.
        /// </summary>
        Task<bool> ClaimPendingAsync(long chatId, int promptMessageId);
    }
}
