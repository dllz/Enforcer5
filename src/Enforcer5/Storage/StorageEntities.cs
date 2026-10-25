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
    /// ban. <c>report_id</c> is in the approved design and kept, but its foreign key arrives with the
    /// reports table in a later step. <c>claimed_at</c>/<c>claimed_by</c> belong to the tempban expiry worker, which also comes
    /// later; tempban expiry is still driven from Redis.
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
    /// nothing makes yet), with who made it and why. <c>report_id</c> gets its foreign key with the
    /// reports table.
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
}
