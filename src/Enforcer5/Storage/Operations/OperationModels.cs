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

    internal sealed record UserStatistic(long UserId, long Messages, DateTimeOffset CapturedAt);
    internal sealed record GroupUserStatistic(long GroupId, long UserId, long Messages,
        DateTimeOffset? LastMessageAt, DateTimeOffset CapturedAt);
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
        internal List<UserStatistic> UserStatistics { get; } = new();
        internal List<GroupUserStatistic> GroupUserStatistics { get; } = new();
        internal List<AccountModerationCounter> ModerationCounters { get; } = new();
        internal List<BotStatisticBaseline> BotStatisticBaselines { get; } = new();
        internal List<AuditEvent> AuditEvents { get; } = new();
        internal List<Exception> Errors { get; } = new();
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
        internal int UserStatisticsWritten { get; set; }
        internal int GroupUserStatisticsWritten { get; set; }
        internal int ModerationCountersWritten { get; set; }
        internal int BotStatisticBaselinesWritten { get; set; }
        internal int AuditEventsWritten { get; set; }
        internal IReadOnlyList<Exception> Errors { get; set; } = Array.Empty<Exception>();
    }
}
