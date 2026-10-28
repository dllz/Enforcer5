using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>A member an admin set back to new (<c>member_trust_overrides</c>).</summary>
    internal sealed record TrustOverride(long ChatId, long UserId, long? SetBy, DateTimeOffset SetAt, long MessagesAtSet,
        DateTimeOffset? RestorePromptedAt);

    /// <summary>A change Detection made, or proposed, to a group's levels (<c>detection_adjustments</c>).</summary>
    internal sealed record Adjustment(long Id, long ChatId, string Kind, string Reason, int? FromValue, int? FromDays,
        int? ToValue, int? ToDays, long? UserId, bool Applied, DateTimeOffset? UndoneAt, DateTimeOffset MadeAt)
    {
        internal const string Established = "established";
        internal const string ReportAt = "report_at";
    }

    /// <summary>One decision about a member Detection had seen signs of: their highest score before it, and whether they were removed or muted (true) or fine.</summary>
    internal sealed record ScoredDecision(int Score, bool Bad);

    /// <summary>
    /// What Detection learns a group's levels from and keeps about it (build step 7b): members set back to
    /// new, the changes made, and the decisions and signs they are based on.
    /// </summary>
    internal interface IDetectionLearningRepository
    {
        Task<IReadOnlyList<TrustOverride>> AllOverridesAsync();

        /// <summary>Sets the member back to new; false when they were already.</summary>
        Task<bool> SetOverrideAsync(long chatId, long userId, long setBy, long messagesAtSet, DateTimeOffset at);

        /// <summary>Restores the member; false when they were not set back to new.</summary>
        Task<bool> RemoveOverrideAsync(long chatId, long userId);

        Task MarkRestorePromptedAsync(long chatId, long userId, DateTimeOffset at);

        /// <summary>
        /// Whether an abuse sign or a decision about abuse has been recorded about the member since
        /// <paramref name="since"/>. The group's own rules (a warn, a game rule) say nothing about trust.
        /// </summary>
        Task<bool> AbuseSinceAsync(long chatId, long userId, DateTimeOffset since);

        /// <summary>The abuse signs recorded for the member between the two times, first seen first.</summary>
        Task<IReadOnlyList<string>> AbuseSignsBetweenAsync(long chatId, long userId, DateTimeOffset from, DateTimeOffset to);

        /// <summary>
        /// Counts of the decisions against the member in the group since <paramref name="since"/>: about abuse,
        /// and about the group's own rules (warns included). The rest is not known.
        /// </summary>
        Task<(int Abuse, int GroupRules)> RecordAsync(long chatId, long userId, DateTimeOffset since);

        Task<long> AddAdjustmentAsync(long chatId, string kind, string reason, int? fromValue, int? fromDays, int? toValue,
            int? toDays, long? userId, long? labelId, bool applied, DateTimeOffset at);

        Task<Adjustment> GetAdjustmentAsync(long id);

        Task MarkUndoneAsync(long id, long by, DateTimeOffset at);

        Task<IReadOnlyList<Adjustment>> RecentAdjustmentsAsync(long chatId, int limit);

        /// <summary>
        /// Admins' decisions about abuse since <paramref name="since"/> about members who had a score: their highest
        /// score in the day before, and whether they were removed or muted, or found fine (or the decision undone).
        /// Decisions about a group's own rules are left out: the score is about abuse.
        /// </summary>
        Task<IReadOnlyList<ScoredDecision>> ScoredDecisionsAsync(long chatId, DateTimeOffset since);

        /// <summary>The groups with something to learn or look after: decisions since <paramref name="since"/>, learned levels, or members set back to new.</summary>
        Task<IReadOnlyList<long>> GroupsToLearnAsync(DateTimeOffset since);
    }
}
