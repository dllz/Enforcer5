using System;
using System.Collections.Generic;

namespace Enforcer5.Storage.Operations
{
    internal static class Editions
    {
        internal const string Normal = "normal";
        internal const string Premium = "premium";
        internal const string Shared = "shared";
    }

    internal static class SanctionKinds
    {
        internal const string Ban = "ban";
        internal const string Mute = "mute";
        internal const string TempBan = "tempban";
        internal const string TempMute = "tempmute";
    }

    internal static class SanctionStatuses
    {
        internal const string Active = "active";
        internal const string Due = "due";
        internal const string Revoked = "revoked";
        internal const string Completed = "completed";
    }

    internal sealed record Sanction(long Id, long GroupId, long UserId, string Kind, string Status,
        string Edition, long? ActorUserId, string Reason, DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiresAt, DateTimeOffset? ClaimedAt, string ClaimedBy,
        DateTimeOffset? ResolvedAt, string LegacyKey);

    internal sealed record GlobalSanction(long UserId, bool Active, string Reason,
        DateTimeOffset? CreatedAt, string RevocationReason, DateTimeOffset? RevokedAt,
        string LegacyKey);

    internal sealed record Report(long Id, long GroupId, long ReportMessageId, long? ReporterUserId,
        string ReporterDisplay, long? ReportedUserId, long? ReportedMessageId, string Status,
        DateTimeOffset? CreatedAt, long? SolvedByUserId, string SolvedByDisplay,
        DateTimeOffset? SolvedAt, string LegacyKey);

    internal sealed record ReportDelivery(long ReportId, int DeliveryIndex, long AdminChatId,
        int AdminMessageId, int? ForwardedMessageId, string LegacyPayload);

    /// <summary>
    /// When a user was last seen speaking in a group, from <c>chat:{groupId}:userlast</c>.
    /// <para>
    /// This is all that remains of the statistics the operations domain used to carry. The counts
    /// themselves - 4,752,053 user totals and 7,383,778 per-group totals - are loaded set-wise by
    /// <c>Storage/Loading</c>, which also computes the unattributed residual that keeps
    /// <c>message_count = SUM(per-group) + unattributed_count</c> exact. Writing them from here as
    /// well meant ~12.1M single-row transactions and an overwrite that did not recompute the
    /// residual.
    /// </para>
    /// </summary>
    internal sealed record GroupUserLastSeen(long GroupId, long UserId, DateTimeOffset LastMessageAt);
    internal sealed record BotStatisticBaseline(string Edition, long Messages, long Users,
        long Bans, long Kicks, DateTimeOffset CapturedAt);
    internal sealed record AuditEvent(long Id, string Edition, string EventType, long? GroupId,
        long? ActorUserId, long? SubjectUserId, string Details, DateTimeOffset OccurredAt,
        string LegacyKey);

    internal enum LegacyParseStatus
    {
        Success,
        Ambiguous,
        Malformed
    }

    internal sealed record LegacyParseResult<T>(LegacyParseStatus Status, T Value, Exception Error)
    {
        internal bool IsSuccess => Status == LegacyParseStatus.Success;
        internal static LegacyParseResult<T> Success(T value) => new(LegacyParseStatus.Success, value, null);
        internal static LegacyParseResult<T> Ambiguous(T value, Exception error) =>
            new(LegacyParseStatus.Ambiguous, value, error);
        internal static LegacyParseResult<T> Malformed(Exception error) =>
            new(LegacyParseStatus.Malformed, default, error);
    }

    internal sealed record LegacyTimestamp(DateTime WallClock, DateTimeOffset? Instant);
    internal sealed record LegacyTempBan(long GroupId, long UserId, string UserName,
        string GroupName, DateTimeOffset ExpiresAt, string Edition, string LegacyKey);

    internal sealed class OperationsBackfillSnapshot
    {
        internal List<Sanction> Sanctions { get; } = new();
        internal List<GlobalSanction> GlobalSanctions { get; } = new();
        internal List<Report> Reports { get; } = new();
        internal List<ReportDelivery> ReportDeliveries { get; } = new();
        internal List<GroupUserLastSeen> GroupUserLastSeen { get; } = new();
        internal List<AccountModerationCounter> ModerationCounters { get; } = new();
        internal List<BotStatisticBaseline> BotStatisticBaselines { get; } = new();
        internal List<AuditEvent> AuditEvents { get; } = new();
        /// <summary>
        /// Records that could not be read at all. A non-empty list fails the run.
        /// </summary>
        internal List<Exception> Errors { get; } = new();
        /// <summary>
        /// Records that were read and migrated, but whose source is defective or undecidable - an
        /// ambiguous legacy timestamp, for instance. Separated from <see cref="Errors"/> because
        /// conflating them meant a single questionable value both failed the run and silently
        /// disabled reconciliation, which skips all removals when any error is present.
        /// </summary>
        internal List<Exception> DataQualityIssues { get; } = new();
    }

    /// <summary>
    /// One <c>ban:{userId}</c> field: a lifetime count of a kind of moderation action against a
    /// user, across every group.
    /// </summary>
    internal sealed record AccountModerationCounter(long UserId, string Kind, long Count);

    internal sealed class OperationsBackfillResult
    {
        internal int SanctionsWritten { get; set; }
        internal int GlobalSanctionsWritten { get; set; }
        internal int ReportsWritten { get; set; }
        internal int ReportDeliveriesWritten { get; set; }
        internal int LastSeenApplied { get; set; }
        internal int ModerationCountersWritten { get; set; }
        internal int BotStatisticBaselinesWritten { get; set; }
        internal int AuditEventsWritten { get; set; }
        internal IReadOnlyList<Exception> Errors { get; set; } = Array.Empty<Exception>();
        internal IReadOnlyList<Exception> DataQualityIssues { get; set; } = Array.Empty<Exception>();
    }
}
