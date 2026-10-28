using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Detection;

namespace Enforcer5.Data
{
    /// <summary>One sign that fired, as recorded in <c>detections</c>. Never message text: a hash of it at most.</summary>
    /// <param name="UserId">0 when no member can be seen (a post made as a channel; the channel is in the evidence).</param>
    internal sealed record DetectionRecord(
        long ChatId,
        long UserId,
        string Sign,
        string Phase,
        int Weight,
        int ScoreBefore,
        int ScoreAfter,
        string Action,
        bool Deleted,
        bool Reported,
        string Mode,
        long? MessageId,
        string MessageType,
        string TextHash,
        string MediaUniqueId,
        string TrustLevel,
        long? MemberMessages,
        DateTimeOffset? MemberSince,
        string Evidence,
        string Edition,
        DateTimeOffset At)
    {
        /// <summary><see cref="SignCategory"/>: abuse, or the group's own rules.</summary>
        internal string Category { get; init; } = SignCategory.Abuse;

        /// <summary>For a sign that compares: how alike (0 to 1); null for the rest.</summary>
        internal double? Similarity { get; init; }
    }

    /// <summary>
    /// How a sign did over a time (/detectstats, /detectreview): how often it fired, for how many members (in how many
    /// groups), what the admins decided about them after (removed: ban, kick or tempban; muted; fined; undone), and how
    /// many near misses it had.
    /// </summary>
    internal sealed record SignStat(string Sign, long Hits, long Members, long Groups, long Removed, long Muted, long Fined,
        long Undone, long NearMisses);

    /// <summary>
    /// Hits or near misses of a comparing sign whose similarity falls in [<see cref="From"/>, From + 0.1), and how many of
    /// their members the admins removed or muted (bad) or found fine (fined, or an action undone) afterwards.
    /// </summary>
    internal sealed record SimilarityBucket(string Sign, string Kind, double From, long Count, long Bad, long Fine);

    /// <summary>A comparison under its threshold (<c>detection_near_misses</c>): what a lower threshold would have caught.</summary>
    internal sealed record NearMissRecord(long ChatId, long UserId, string Sign, double Similarity, double Threshold, string Evidence,
        string Edition, DateTimeOffset At);

    /// <summary>
    /// A decision about a member, as recorded in <c>member_labels</c>: what was done (or undone), by
    /// whom, how (<see cref="Source"/>), and what it was about. <see cref="Key"/> makes a decision
    /// both editions saw (a ban made in the Telegram app) one row.
    /// </summary>
    internal sealed record LabelEvent(
        long ChatId,
        long UserId,
        string Label,
        string Action,
        string Source,
        long? ActorId,
        long? MessageId,
        string MessageType,
        string TextHash,
        string MediaUniqueId,
        string Reason,
        string Key,
        string Edition,
        DateTimeOffset At)
    {
        /// <summary>The admin's reason names spam, ads, scams and the like (<see cref="AbuseWords"/>): abuse, whatever the signs.</summary>
        internal bool ReasonIsAbuse { get; init; }
    }

    /// <summary>A decision as recorded: its id, and what it was about (abuse, group, or null when not known).</summary>
    internal sealed record RecordedLabel(long Id, string Category);

    /// <summary>
    /// What Detection learns from: every sign that fired (<c>detections</c>) and every decision about a
    /// member (<c>member_labels</c>), linked both ways: a decision sets the outcome of the member's
    /// detections from the 24 hours before it, and a decision no sign saw coming is marked missed.
    /// </summary>
    internal interface IDetectionLogRepository
    {
        /// <summary>Writes detections of known groups and accounts; returns how many were written.</summary>
        Task<int> WriteDetectionsAsync(IReadOnlyList<DetectionRecord> detections);

        /// <summary>Writes near misses of known groups and accounts; returns how many were written.</summary>
        Task<int> WriteNearMissesAsync(IReadOnlyList<NearMissRecord> nearMisses);

        /// <summary>Every sign's record since <paramref name="since"/>, in one group, or in all with <paramref name="chatId"/> null; live and observe.</summary>
        Task<IReadOnlyList<SignStat>> SignRecordAsync(long? chatId, DateTimeOffset since);

        /// <summary>Admins' removals, mutes and deletes since <paramref name="since"/> that no sign came before (missed), and all of them.</summary>
        Task<(long Missed, long Decisions)> MissedAsync(long? chatId, DateTimeOffset since);

        /// <summary>Hits and near misses of the comparing signs by similarity, across all groups, since <paramref name="since"/>.</summary>
        Task<IReadOnlyList<SimilarityBucket>> SimilarityBucketsAsync(DateTimeOffset since);

        /// <summary>The signs that counted for a member since <paramref name="since"/>, for their score after a restart.</summary>
        Task<IReadOnlyList<MemberScores.Hit>> RecentHitsAsync(long chatId, long userId, DateTimeOffset since);

        /// <summary>Records a decision and links it to the member's detections. Its id; null when the other edition recorded it first.</summary>
        Task<RecordedLabel> RecordLabelAsync(LabelEvent label);

        /// <summary>
        /// Names and usernames of the group's members seen since <paramref name="since"/> (joined, or
        /// posted), newest first, at most <paramref name="limit"/>: what /filter test tries a rule on.
        /// </summary>
        Task<IReadOnlyList<(string Name, string Username)>> RecentMemberNamesAsync(long chatId, DateTimeOffset since, int limit);

        /// <summary>
        /// What watching only found in the group so far: how many members the admins would have been told
        /// about, and how many times something would have been done (deleted, warned, muted, removed).
        /// </summary>
        Task<(long Members, long Actions)> ObservedAsync(long chatId);

        /// <summary>For each of <paramref name="signs"/>: how many times it fired in the group, and for how many members a ban, kick or tempban followed.</summary>
        Task<IReadOnlyDictionary<string, (long Hits, long Removed)>> SignStatsAsync(long chatId, IReadOnlyList<string> signs);

        /// <summary>
        /// The member's Fine decisions since <paramref name="since"/>: the signs each was about (comma
        /// separated, as recorded) and when. Fine exempts them from those signs (design section 6).
        /// </summary>
        Task<IReadOnlyList<(string Signs, DateTimeOffset At)>> FinesAsync(long chatId, long userId, DateTimeOffset since);

        /// <summary>The live signs that counted or reported for a member since <paramref name="since"/>, with how often: a report's contents after a restart.</summary>
        Task<IReadOnlyList<(string Sign, int Count)>> SignsSinceAsync(long chatId, long userId, DateTimeOffset since);
    }
}
