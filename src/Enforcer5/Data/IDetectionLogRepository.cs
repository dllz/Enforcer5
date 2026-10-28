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
        DateTimeOffset At);

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
        DateTimeOffset At);

    /// <summary>
    /// What Detection learns from: every sign that fired (<c>detections</c>) and every decision about a
    /// member (<c>member_labels</c>), linked both ways: a decision sets the outcome of the member's
    /// detections from the 24 hours before it, and a decision no sign saw coming is marked missed.
    /// </summary>
    internal interface IDetectionLogRepository
    {
        /// <summary>Writes detections of known groups and accounts; returns how many were written.</summary>
        Task<int> WriteDetectionsAsync(IReadOnlyList<DetectionRecord> detections);

        /// <summary>The signs that counted for a member since <paramref name="since"/>, for their score after a restart.</summary>
        Task<IReadOnlyList<MemberScores.Hit>> RecentHitsAsync(long chatId, long userId, DateTimeOffset since);

        /// <summary>Records a decision and links it to the member's detections.</summary>
        Task RecordLabelAsync(LabelEvent label);

        /// <summary>
        /// Names and usernames of the group's members seen since <paramref name="since"/> (joined, or
        /// posted), newest first, at most <paramref name="limit"/>: what /filter test tries a rule on.
        /// </summary>
        Task<IReadOnlyList<(string Name, string Username)>> RecentMemberNamesAsync(long chatId, DateTimeOffset since, int limit);

        /// <summary>For each of <paramref name="signs"/>: how many times it fired in the group, and for how many members a ban, kick or tempban followed.</summary>
        Task<IReadOnlyDictionary<string, (long Hits, long Removed)>> SignStatsAsync(long chatId, IReadOnlyList<string> signs);
    }
}
