using System;

namespace Enforcer5.Storage
{
    /// <summary>
    /// Lifetime count of moderation actions taken against a user, by kind, across every group.
    /// <para>
    /// The Redis original is <c>ban:{userId}</c>, a hash of counters such as <c>flood</c>,
    /// <c>kick</c> and <c>longmessages</c>. It is user-tier rather than group-tier, written on the
    /// message hot path from nineteen call sites, and read only by <c>/getuser</c>. It had no
    /// storage routing on either side and nothing under Storage/ touched it, so it would have been
    /// left behind entirely.
    /// </para>
    /// </summary>
    internal sealed class AccountModerationCounterEntity
    {
        public long AccountId { get; set; }
        public string Kind { get; set; }
        public long Count { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
    }

    /// <summary>
    /// Per-group cutover state and lease.
    /// <para>
    /// This is what makes the group the migration unit. It replaces the assumption that the single
    /// global <c>MigrationMaintenanceGuard</c> lock could serve as a per-group lease - that lock
    /// requires both bot processes to be stopped, which is not a per-group migration.
    /// </para>
    /// </summary>
    internal sealed class GroupMigrationStateEntity
    {
        public long TelegramChatId { get; set; }
        public string State { get; set; }
        /// <summary>Owner of the current cutover attempt, null when unleased.</summary>
        public string LeaseOwner { get; set; }
        /// <summary>Lease expiry. A crashed migrator's lease is reclaimable once this passes.</summary>
        public DateTimeOffset? LeaseExpiresAt { get; set; }
        /// <summary>Resume point for a chunked copy, so an interrupted large group continues.</summary>
        public long ChunkCursor { get; set; }
        public string LastError { get; set; }
        public DateTimeOffset? MigratedAt { get; set; }
        public DateTimeOffset? ReconciledAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

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

    internal sealed class UserEntity
    {
        public long UserId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class TelegramAccountEntity
    {
        public long AccountId { get; set; }
        public long TelegramUserId { get; set; }
        public bool IsBot { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    internal sealed class TelegramAccountProfileEntity
    {
        public long ProfileId { get; set; }
        public long AccountId { get; set; }
        public string DisplayName { get; set; }
        public string Username { get; set; }
        public string LanguageCode { get; set; }
        public DateTimeOffset ValidFrom { get; set; }
        public DateTimeOffset? ValidTo { get; set; }
    }

    internal sealed class UserIdentityLinkEntity
    {
        public long IdentityLinkId { get; set; }
        public long UserId { get; set; }
        public long AccountId { get; set; }
        public long? LinkedByUserId { get; set; }
        public long? UnlinkedByUserId { get; set; }
        public string Source { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset LinkedAt { get; set; }
        public DateTimeOffset? UnlinkedAt { get; set; }
    }

    internal sealed class TelegramChatEntity
    {
        public long ChatId { get; set; }
        public long TelegramChatId { get; set; }
        public string ChatType { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    internal sealed class GroupEntity
    {
        public long GroupId { get; set; }
        public long ChatId { get; set; }
        public string Title { get; set; }
        public string Username { get; set; }
        public string PublicLink { get; set; }
        public bool Active { get; set; }
        public long ObservedUserCount { get; set; }
        public DateTimeOffset? LastMessageAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class TelegramChatProfileEntity
    {
        public long ChatProfileId { get; set; }
        public long ChatId { get; set; }
        public string Title { get; set; }
        public string Username { get; set; }
        public DateTimeOffset ValidFrom { get; set; }
        public DateTimeOffset? ValidTo { get; set; }
    }

    internal sealed class GroupMembershipPeriodEntity
    {
        public long MembershipId { get; set; }
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public long? UserIdAtTime { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public DateTimeOffset? JoinedAt { get; set; }
        public DateTimeOffset? LeftAt { get; set; }
    }

    internal sealed class GroupAccountObservationEntity
    {
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    internal sealed class GroupUserObservationEntity
    {
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    internal sealed class GroupSettingsEntity
    {
        public long GroupId { get; set; }
        public long? LogChatId { get; set; }
        public bool ReportsEnabled { get; set; }
        public bool WelcomeEnabled { get; set; }
        public bool FloodProtectionEnabled { get; set; }
        public bool DeleteLastWelcome { get; set; }
        public bool MuteOnJoin { get; set; }
        public short MaximumMessages { get; set; }
        public string FloodAction { get; set; }
        public short MaximumWarnings { get; set; }
        public string WarningAction { get; set; }
        public short MediaWarningLimit { get; set; }
        public string MediaAction { get; set; }
        public int TempbanMinutes { get; set; }
        public int TempmuteMinutes { get; set; }
        public string WelcomeFormat { get; set; }
        public string WelcomeContent { get; set; }
        public string WelcomeMediaFileId { get; set; }
        public string WelcomeMediaFileOwnerEdition { get; set; }
        public bool RulesPrivateOnly { get; set; }
        public bool AboutPrivateOnly { get; set; }
        public bool ModlistPrivateOnly { get; set; }
        public bool ExtraPrivateOnly { get; set; }
        public bool HelpPrivateOnly { get; set; }
        public long Version { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class GroupFloodExceptionEntity { public long GroupId { get; set; } public string ContentType { get; set; } public bool Excluded { get; set; } }
    internal sealed class GroupMediaPolicyEntity { public long GroupId { get; set; } public string MediaType { get; set; } public bool Allowed { get; set; } }
    internal sealed class GroupCharacterPolicyEntity { public long GroupId { get; set; } public string CharacterClass { get; set; } public string Action { get; set; } }
    internal sealed class GroupLengthPolicyEntity { public long GroupId { get; set; } public string Scope { get; set; } public bool Enabled { get; set; } public int MaximumCharacters { get; set; } public int MaximumLines { get; set; } public string Action { get; set; } }
    internal sealed class GroupContentEntity { public long GroupId { get; set; } public string ContentType { get; set; } public string Content { get; set; } }
    internal sealed class CustomCommandEntity { public long GroupId { get; set; } public string Trigger { get; set; } public string Content { get; set; } public string MediaType { get; set; } public string FileId { get; set; } public string FileOwnerEdition { get; set; } public string Caption { get; set; } }
    internal sealed class CustomCommandConflictCandidateEntity { public Guid CandidateId { get; set; } public long GroupId { get; set; } public string Trigger { get; set; } public string SourceTrigger { get; set; } public string Content { get; set; } public string MediaType { get; set; } public string FileId { get; set; } public string FileOwnerEdition { get; set; } public string Caption { get; set; } public DateTimeOffset? ResolvedAt { get; set; } public string Resolution { get; set; } public Guid? SelectedCandidateId { get; set; } }
    internal sealed class GroupEntitlementEntity { public long GroupId { get; set; } public string Entitlement { get; set; } public bool Enabled { get; set; } public DateTimeOffset? ExpiresAt { get; set; } }

    internal sealed class GroupRoleAssignmentEntity { public long AssignmentId { get; set; } public long GroupId { get; set; } public long UserId { get; set; } public string Role { get; set; } public DateTimeOffset AssignedAt { get; set; } public DateTimeOffset? RevokedAt { get; set; } }
    internal sealed class GroupElevationBlockEntity { public long BlockId { get; set; } public long GroupId { get; set; } public long UserId { get; set; } public DateTimeOffset BlockedAt { get; set; } public DateTimeOffset? RevokedAt { get; set; } }
    internal sealed class GroupUserPreferenceEntity { public long GroupId { get; set; } public long UserId { get; set; } public bool AdminNotificationsDisabled { get; set; } public bool ReportsBlocked { get; set; } public bool WatchDisabled { get; set; } public bool TagAllSubscribed { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
    internal sealed class GlobalRoleAssignmentEntity { public long AssignmentId { get; set; } public long UserId { get; set; } public string Role { get; set; } public DateTimeOffset AssignedAt { get; set; } public DateTimeOffset? RevokedAt { get; set; } }

    internal sealed class WarningBalanceEntity { public long GroupId { get; set; } public long UserId { get; set; } public string WarningType { get; set; } public long ActiveQuantity { get; set; } public long LifetimeQuantity { get; set; } }
    internal sealed class WarningEventEntity { public long WarningEventId { get; set; } public long GroupId { get; set; } public long AccountId { get; set; } public long? UserIdAtTime { get; set; } public long? ActorUserId { get; set; } public long? ReportId { get; set; } public string WarningType { get; set; } public string Operation { get; set; } public long Quantity { get; set; } public string Reason { get; set; } public DateTimeOffset OccurredAt { get; set; } public string LegacyKey { get; set; } }

    internal sealed class SanctionEntity
    {
        public long SanctionId { get; set; }
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public long? UserIdAtTime { get; set; }
        public long? ActorUserId { get; set; }
        public long? TriggerWarningEventId { get; set; }
        public long? ReportId { get; set; }
        public string Kind { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset ImposedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public string Edition { get; set; }
        public DateTimeOffset? ClaimedAt { get; set; }
        public string ClaimedBy { get; set; }
        public string LegacyKey { get; set; }
    }

    internal sealed class GlobalSanctionEntity { public long GlobalSanctionId { get; set; } public long AccountId { get; set; } public long? UserIdAtTime { get; set; } public long? ActorUserId { get; set; } public string Kind { get; set; } public string Status { get; set; } public string Reason { get; set; } public DateTimeOffset ImposedAt { get; set; } public DateTimeOffset? ExpiresAt { get; set; } public DateTimeOffset? ResolvedAt { get; set; } public string LegacyKey { get; set; } }
    internal sealed class ReportEntity { public long ReportId { get; set; } public long GroupId { get; set; } public long? ReporterUserId { get; set; } public long? ReporterAccountId { get; set; } public string ReporterDisplay { get; set; } public long? TargetUserId { get; set; } public long? TargetAccountId { get; set; } public string TargetDisplay { get; set; } public long? SolverUserId { get; set; } public string SolverDisplay { get; set; } public long ReportMessageId { get; set; } public long? TargetMessageId { get; set; } public string Status { get; set; } public string ResolutionAction { get; set; } public DateTimeOffset ReportedAt { get; set; } public DateTimeOffset? ResolvedAt { get; set; } public string LegacyKey { get; set; } }
    internal sealed class MessageMetadataEntity { public long GroupId { get; set; } public long TelegramMessageId { get; set; } public long? AccountId { get; set; } public long? UserIdAtTime { get; set; } public long? ReplyToMessageId { get; set; } public string MessageType { get; set; } public bool HasMedia { get; set; } public DateTimeOffset SentAt { get; set; } public DateTimeOffset? EditedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }

    internal sealed class GroupDailyAnalyticsEntity { public long GroupId { get; set; } public DateOnly Day { get; set; } public long MessageCount { get; set; } public long ActiveUserCount { get; set; } public long JoinCount { get; set; } public long LeaveCount { get; set; } }
    internal sealed class GroupAccountDailyAnalyticsEntity { public long GroupId { get; set; } public long AccountId { get; set; } public DateOnly Day { get; set; } public long MessageCount { get; set; } public DateTimeOffset? FirstMessageAt { get; set; } public DateTimeOffset? LastMessageAt { get; set; } }
    internal sealed class GroupUserDailyAnalyticsEntity { public long GroupId { get; set; } public long UserId { get; set; } public DateOnly Day { get; set; } public long MessageCount { get; set; } public DateTimeOffset? FirstMessageAt { get; set; } public DateTimeOffset? LastMessageAt { get; set; } }
    /// <summary>
    /// A user's lifetime message total.
    /// <para>
    /// <c>MessageCount</c> is authoritative and is carried from Redis rather than derived. Measured
    /// across the production dataset, <c>chat:{userId}.msgs</c> is greater than or equal to the sum
    /// of that user's per-group counters for 100% of 4.7M users and never lower - the two are
    /// incremented together, so they only diverge when a per-group key is deleted and the global one
    /// survives. Recomputing the total from group sums would have lost 27,559,870 messages, 2.36% of
    /// all volume, and zeroed 34,736 users outright.
    /// </para>
    /// <para>
    /// <c>UnattributedCount</c> holds the residual that no surviving group accounts for, so
    /// <c>MessageCount = SUM(group_account_message_totals) + UnattributedCount</c> holds exactly and
    /// stays checkable after cutover.
    /// </para>
    /// </summary>
    internal sealed class AccountMessageTotalEntity { public long AccountId { get; set; } public long MessageCount { get; set; } public long UnattributedCount { get; set; } public DateTimeOffset CapturedAt { get; set; } }
    internal sealed class GroupAccountMessageTotalEntity { public long GroupId { get; set; } public long AccountId { get; set; } public long MessageCount { get; set; } public DateTimeOffset? LastMessageAt { get; set; } public DateTimeOffset CapturedAt { get; set; } }
    internal sealed class BotStatisticBaselineEntity { public string Edition { get; set; } public long MessageCount { get; set; } public long UserCount { get; set; } public long BanCount { get; set; } public long KickCount { get; set; } public DateTimeOffset CapturedAt { get; set; } }
    internal sealed class AnalyticsFlushBatchEntity { public Guid BatchId { get; set; } public string Edition { get; set; } public string Source { get; set; } public DateTimeOffset CommittedAt { get; set; } }
    internal sealed class AnalyticsFlushBatchItemEntity { public Guid BatchId { get; set; } public long GroupId { get; set; } public long AccountId { get; set; } public DateOnly Day { get; set; } public long MessageDelta { get; set; } public long ActiveUserDelta { get; set; } public long JoinDelta { get; set; } public long LeaveDelta { get; set; } }
    internal sealed class GroupAuditEventEntity { public long EventId { get; set; } public long GroupId { get; set; } public long? ActorUserId { get; set; } public long? SubjectUserId { get; set; } public string EventType { get; set; } public DateTimeOffset OccurredAt { get; set; } public string LegacyKey { get; set; } }
}
