using System;

namespace Enforcer5.Storage
{
    // The PostgreSQL schema, introduced one domain at a time. Every entity here is copied
    // unchanged from the approved design on feature/postgres-migration (docs/storage-erd.md), so
    // later steps add tables beside these rather than reshaping them. Do not "simplify" a column
    // away because nothing reads it yet.

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

    /// <summary>
    /// One row per sanction. A new ban is a new row; lifting it sets <c>status</c> to
    /// <c>revoked</c> (an unban) or <c>completed</c> (a tempban that ran out) and fills
    /// <c>resolved_at</c>, so the rows for a group and account are its ban history.
    /// <para>
    /// <c>trigger_warning_event_id</c> is the warn that reached the group's maximum, for a max-warns
    /// ban. <c>report_id</c> is the report the sanction was taken from. <c>claimed_at</c>/<c>claimed_by</c>
    /// belong to the tempban expiry worker, which comes later; tempban expiry is still driven from Redis.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// A global ban: banned in every group Enforcer moderates, by a global admin. One row per ban;
    /// lifting it (<c>/unrekt</c>) sets <c>status</c> to <c>revoked</c> with <c>resolved_at</c> and
    /// <c>resolution_reason</c>, and at most one row per account is <c>active</c>. Since step 7 this
    /// table is what global bans run on; the old <c>globalBan:{id}</c> Redis hashes were imported
    /// once (<c>legacy_key</c>) and are no longer read or written.
    /// <para>
    /// <c>notified_at</c> is not in the approved design: when the support chat was last told that
    /// this banned account is there (Redis kept it as a <c>notified</c> field), so it is told once
    /// per ban.
    /// </para>
    /// </summary>
    internal sealed class GlobalSanctionEntity
    {
        public long GlobalSanctionId { get; set; }
        public long AccountId { get; set; }
        public long? UserIdAtTime { get; set; }
        public long? ActorUserId { get; set; }
        public string Kind { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset ImposedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public string ResolutionReason { get; set; }
        public string LegacyKey { get; set; }
        public DateTimeOffset? NotifiedAt { get; set; }
    }

    /// <summary>
    /// A member's current and lifetime warn count of one type in one group: <c>standard</c>
    /// (<c>chat:{id}:warns</c> / <c>totalWarns</c>), <c>prewarning</c> (<c>prewarns</c>) or
    /// <c>media</c> (<c>mediawarn</c>).
    /// </summary>
    internal sealed class WarningBalanceEntity
    {
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string WarningType { get; set; }
        public long ActiveQuantity { get; set; }
        public long LifetimeQuantity { get; set; }
    }

    /// <summary>
    /// One change to a warn balance: <c>add</c>, <c>remove</c>, <c>reset</c> (or <c>decay</c>, which
    /// nothing makes yet), with who made it and why, and the report it was made from.
    /// </summary>
    internal sealed class WarningEventEntity
    {
        public long WarningEventId { get; set; }
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public long? UserIdAtTime { get; set; }
        public long? ActorUserId { get; set; }
        public long? ReportId { get; set; }
        public string WarningType { get; set; }
        public string Operation { get; set; }
        public long Quantity { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public string LegacyKey { get; set; }
    }

    /// <summary>
    /// A report made with /report, /admin or @admin: who reported whom (the target is known only
    /// when the report replied to a message), the report message and the reported message, and how
    /// it was resolved. The Telegram message ids of the copies sent to admins stay in Redis, as the
    /// approved design has it. The foreign keys to message_metadata in that design arrive with that
    /// table.
    /// </summary>
    internal sealed class ReportEntity
    {
        public long ReportId { get; set; }
        public long GroupId { get; set; }
        public long? ReporterUserId { get; set; }
        public long? ReporterAccountId { get; set; }
        public string ReporterDisplay { get; set; }
        public long? TargetUserId { get; set; }
        public long? TargetAccountId { get; set; }
        public string TargetDisplay { get; set; }
        public long? SolverUserId { get; set; }
        public string SolverDisplay { get; set; }
        public long ReportMessageId { get; set; }
        public long? TargetMessageId { get; set; }
        public string Status { get; set; }
        public string ResolutionAction { get; set; }
        public DateTimeOffset ReportedAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public string LegacyKey { get; set; }
    }

    /// <summary>
    /// Every action an admin took from a report's buttons (delete, warn, kick, ban), several per
    /// report if need be. Not in the approved design: added in step 6 because a report is resolved
    /// only by its Solved button, and the actions taken before that would otherwise be lost (the
    /// design's single resolution_action holds one).
    /// </summary>
    internal sealed class ReportActionEntity
    {
        public long ReportActionId { get; set; }
        public long ReportId { get; set; }
        public long? ActorUserId { get; set; }
        public string Action { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
    }

    // Raid building blocks (not in the approved design; added 2026-10 for raid detection and the
    // after-the-fact lookup: who joined with whom, from which link, saying what). See
    // docs/postgres-rollout.md, step 8.

    /// <summary>
    /// One member of one group as the bot has seen them: when first seen there, the latest join
    /// (how, and by whom if added), how many messages and when the first and last were. Counts start
    /// from the Redis message counter the first time a member is written, so existing members are
    /// not new. The basis of the trust levels.
    /// </summary>
    internal sealed class GroupMemberEntity
    {
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset? JoinedAt { get; set; }
        public string JoinSource { get; set; }
        public string InviteLinkName { get; set; }
        public long? AddedByAccountId { get; set; }
        public long MessageCount { get; set; }
        public DateTimeOffset? FirstMessageAt { get; set; }
        public DateTimeOffset? LastMessageAt { get; set; }
        public DateTimeOffset? LeftAt { get; set; }
    }

    /// <summary>
    /// Every join: when, how (<c>link</c>, <c>added</c> by a member, <c>request</c> approved,
    /// <c>unknown</c> when only the service message was seen), the invite link (its name and the link
    /// itself, which belongs to the group) and who added them. Kept 180 days.
    /// </summary>
    internal sealed class GroupJoinEntity
    {
        public long JoinId { get; set; }
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public DateTimeOffset JoinedAt { get; set; }
        public string Source { get; set; }
        public string InviteLink { get; set; }
        public string InviteLinkName { get; set; }
        public long? AddedByAccountId { get; set; }
    }

    /// <summary>
    /// What a message was like, never what it said: its type, a hash of its normalised text, the
    /// id Telegram gives the same file everywhere, the script it was written in and its links. For
    /// each member's first messages, a sample after that, and everything during a raid
    /// (<c>sample_reason</c> <c>first</c>, <c>sample</c>, <c>raid</c>). Kept 30 days.
    /// </summary>
    internal sealed class MessageFingerprintEntity
    {
        public long FingerprintId { get; set; }
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public long TelegramMessageId { get; set; }
        public DateTimeOffset SentAt { get; set; }
        public string ContentType { get; set; }
        public string TextHash { get; set; }
        public string MediaUniqueId { get; set; }
        public string Script { get; set; }
        public int LinkCount { get; set; }
        public bool BareLink { get; set; }
        public string SampleReason { get; set; }
    }

    /// <summary>
    /// A group's normal, recomputed nightly from <see cref="GroupMemberEntity"/>: what an established
    /// member there looks like (messages and days, scaled to how active the group is) and how many
    /// join in a usual hour.
    /// </summary>
    internal sealed class GroupStatsEntity
    {
        public long GroupId { get; set; }
        public DateTimeOffset ComputedAt { get; set; }
        public int ActiveMembers { get; set; }
        public int MedianMessages { get; set; }
        public double MedianTenureDays { get; set; }
        public int EstablishedMessages { get; set; }
        public int EstablishedDays { get; set; }
        public double JoinsPerHour { get; set; }
    }
}
