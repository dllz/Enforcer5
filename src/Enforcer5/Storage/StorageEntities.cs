using System;

namespace Enforcer5.Storage
{
    internal sealed class MigrationRun
    {
        public long Id { get; set; }
        public string Mode { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string Status { get; set; }
        public string SourceSnapshot { get; set; }
        public string Details { get; set; }
    }

    internal sealed class MigrationException
    {
        public long Id { get; set; }
        public long? MigrationRunId { get; set; }
        public string Domain { get; set; }
        public string SourceKey { get; set; }
        public string SourceField { get; set; }
        public string Severity { get; set; }
        public string Reason { get; set; }
        public string RawValue { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public string Resolution { get; set; }
    }

    internal sealed class LegacyRedisRecord
    {
        public string SourceKey { get; set; }
        public string SourceField { get; set; }
        public string SourceType { get; set; }
        public string RawValue { get; set; }
        public long? SourceTtlMs { get; set; }
        public string SourceChecksum { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
        public long? MigrationRunId { get; set; }
        public string Disposition { get; set; }
        public string TargetReference { get; set; }
    }

    internal sealed class CompatibilityOutboxEntry
    {
        public Guid Id { get; set; }
        public string Domain { get; set; }
        public string OrderingKey { get; set; }
        public string Operation { get; set; }
        public string Payload { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; }
        public int AttemptCount { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string LastError { get; set; }
    }

    internal sealed class GroupSettingsEntity
    {
        public long ChatId { get; set; }
        public string SettingGroup { get; set; }
        public string Values { get; set; }
        public long Version { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class GroupSettingsRedisSync
    {
        public long ChatId { get; set; }
        public string SettingGroup { get; set; }
        public string RedisKey { get; set; }
        public bool SourceExists { get; set; }
        public long SourceRevision { get; set; }
        public string SourceChecksum { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public DateTimeOffset PersistedAt { get; set; }
    }

    internal sealed class UserEntity
    {
        public long UserId { get; set; }
        public string Name { get; set; }
        public string Username { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class GroupEntity
    {
        public long ChatId { get; set; }
        public string Name { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class GroupEntitlementEntity
    {
        public long ChatId { get; set; }
        public string Entitlement { get; set; }
        public bool Enabled { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class GroupContentEntity
    {
        public long ChatId { get; set; }
        public string ContentType { get; set; }
        public string Content { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class CustomCommandEntity
    {
        public long ChatId { get; set; }
        public string Trigger { get; set; }
        public string Content { get; set; }
        public string MediaType { get; set; }
        public string FileId { get; set; }
        public string Caption { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class GroupRoleAssignmentEntity
    {
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string Role { get; set; }
        public DateTimeOffset AssignedAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
    }

    internal sealed class GroupElevationBlockEntity
    {
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public DateTimeOffset BlockedAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
    }

    internal sealed class GlobalRoleEntity
    {
        public long UserId { get; set; }
        public string Role { get; set; }
        public bool Active { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class GroupUserPolicyEntity
    {
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string Policy { get; set; }
        public bool Enabled { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class WarningBalanceEntity
    {
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string WarningType { get; set; }
        public long ActiveQuantity { get; set; }
        public long LifetimeQuantity { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class WarningEventEntity
    {
        public long Id { get; set; }
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string WarningType { get; set; }
        public string Operation { get; set; }
        public long Quantity { get; set; }
        public long ActiveQuantity { get; set; }
        public long LifetimeQuantity { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    internal sealed class SanctionEntity
    {
        public long Id { get; set; }
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string Kind { get; set; }
        public string Status { get; set; }
        public string Edition { get; set; }
        public long? ActorUserId { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? ClaimedAt { get; set; }
        public string ClaimedBy { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public string LegacyKey { get; set; }
    }

    internal sealed class GlobalSanctionEntity
    {
        public long UserId { get; set; }
        public bool Active { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public string RevocationReason { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public string LegacyKey { get; set; }
    }

    internal sealed class ReportEntity
    {
        public long Id { get; set; }
        public long GroupId { get; set; }
        public long ReportMessageId { get; set; }
        public long? ReporterUserId { get; set; }
        public string ReporterDisplay { get; set; }
        public long? ReportedUserId { get; set; }
        public long? ReportedMessageId { get; set; }
        public string Status { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public long? SolvedByUserId { get; set; }
        public string SolvedByDisplay { get; set; }
        public DateTimeOffset? SolvedAt { get; set; }
        public string LegacyKey { get; set; }
    }

    internal sealed class ReportDeliveryEntity
    {
        public long ReportId { get; set; }
        public int DeliveryIndex { get; set; }
        public long AdminChatId { get; set; }
        public int AdminMessageId { get; set; }
        public int? ForwardedMessageId { get; set; }
        public string LegacyPayload { get; set; }
    }

    internal sealed class UserStatisticEntity { public long UserId { get; set; } public long Messages { get; set; } public DateTimeOffset CapturedAt { get; set; } }
    internal sealed class GroupUserStatisticEntity { public long GroupId { get; set; } public long UserId { get; set; } public long Messages { get; set; } public DateTimeOffset? LastMessageAt { get; set; } public DateTimeOffset CapturedAt { get; set; } }
    internal sealed class BotStatisticBaselineEntity { public string Edition { get; set; } public long Messages { get; set; } public long Users { get; set; } public long Bans { get; set; } public long Kicks { get; set; } public DateTimeOffset CapturedAt { get; set; } }
    internal sealed class AuditEventEntity { public long Id { get; set; } public string Edition { get; set; } public string EventType { get; set; } public long? GroupId { get; set; } public long? ActorUserId { get; set; } public long? SubjectUserId { get; set; } public string Details { get; set; } public DateTimeOffset OccurredAt { get; set; } public string LegacyKey { get; set; } }
}
