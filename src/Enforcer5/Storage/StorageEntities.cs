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

    /// <summary>
    /// One raid (or possible raid the admins were asked about), for telemetry and replay: how it
    /// started and ended, how bad it got, what was done and how fast. Kept 90 days.
    /// </summary>
    internal sealed class RaidIncidentEntity
    {
        public long IncidentId { get; set; }
        public string IncidentKey { get; set; }
        public long GroupId { get; set; }
        public string Edition { get; set; }
        public string Trigger { get; set; }
        public string PhaseReached { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? ActivatedAt { get; set; }
        public DateTimeOffset? LockedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string EndedBy { get; set; }
        public int PeakScore { get; set; }
        public string[] Reasons { get; set; }
        public int Joined { get; set; }
        public int Muted { get; set; }
        public int Banned { get; set; }
    }

    /// <summary>
    /// The timeline of a raid: joins, messages, signs, actions, 429s, at their offset from its start
    /// (negative for the joins that set it off). People are a hash that only means something within
    /// the incident (<c>actor</c>), never an account id; messages are their fingerprint, never text.
    /// </summary>
    internal sealed class RaidEventEntity
    {
        public long EventId { get; set; }
        public long IncidentId { get; set; }
        public long OffsetMs { get; set; }
        public string Kind { get; set; }
        public string Actor { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>
    /// The columns of the approved <c>group_settings</c> that the automatic checks use (Detection v2
    /// step 2), with the design's types and constraints. Its other columns (welcome, reports, the
    /// private-only commands) arrive with their domains. The row's existence says the group's
    /// settings were imported from Redis; <c>version</c> goes up with every change of the group's
    /// detection settings, here or in <c>detection_policies</c> and <c>group_filters</c>.
    /// </summary>
    internal sealed class GroupSettingsEntity
    {
        public long GroupId { get; set; }
        public short MaximumWarnings { get; set; }
        public string WarningAction { get; set; }
        public int TempbanMinutes { get; set; }
        public int TempmuteMinutes { get; set; }
        public long Version { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// One automatic check of one group (Detection v2, docs/detection-design.md section 12): whether
    /// it is on, its action, its weight in the score, and its thresholds and lists as
    /// <c>settings</c>. Replaces the approved design's <c>group_flood_exceptions</c> and
    /// <c>group_media_policies</c> (Daniel, 2026-10-27).
    /// </summary>
    internal sealed class DetectionPolicyEntity
    {
        public long GroupId { get; set; }
        public string CheckKey { get; set; }
        public bool Enabled { get; set; }
        public string Action { get; set; }
        public short? Weight { get; set; }
        public string Settings { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// A rule of one group: a preset (Arabic script, right-to-left marks), a length rule (text,
    /// name), an admin's regex, or an entry of a blocklist (inline bots by username or regex,
    /// sticker packs by name). Replaces the approved design's <c>group_character_policies</c> and
    /// <c>group_length_policies</c>. <c>mode</c> trial records what a rule would do without acting.
    /// </summary>
    internal sealed class GroupFilterEntity
    {
        public long FilterId { get; set; }
        public long GroupId { get; set; }
        public string Target { get; set; }
        public string Kind { get; set; }
        public string Preset { get; set; }
        public string Pattern { get; set; }
        public int? MaxCharacters { get; set; }
        public int? MaxLines { get; set; }
        public string Action { get; set; }
        public bool Enabled { get; set; }
        public string Mode { get; set; }
        public DateTimeOffset? TrialUntil { get; set; }
        public long? CreatedByAccountId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public long Hits { get; set; }

        /// <summary>When the admins were told the rule's trial is over (build step 7); cleared when the trial is extended.</summary>
        public DateTimeOffset? TrialNotifiedAt { get; set; }

        /// <summary>An admin's own rule: abuse (marked with /filter spam) or group, the default (step 7c).</summary>
        public string Category { get; set; }
    }

    /// <summary>
    /// An admin of a group who does not want its Detection reports in private (design section 6).
    /// </summary>
    internal sealed class ReportAdminOptOutEntity
    {
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public DateTimeOffset OptedOutAt { get; set; }
    }

    /// <summary>
    /// A member an admin set back to new with /untrust (build step 7b): new in the group whatever their
    /// counts, until an admin restores them. <c>messages_at_set</c> is their message count then, so the
    /// bot can tell how much they talked since and offer to restore them.
    /// </summary>
    internal sealed class MemberTrustOverrideEntity
    {
        public long GroupId { get; set; }
        public long AccountId { get; set; }
        public long? SetByAccountId { get; set; }
        public DateTimeOffset SetAt { get; set; }
        public long MessagesAtSet { get; set; }
        public DateTimeOffset? RestorePromptedAt { get; set; }
    }

    /// <summary>
    /// A change Detection made to a group's levels from its admins' decisions (build step 7b): the
    /// established level (messages and days) or the score at which members are reported. <c>from</c> and
    /// <c>to</c> are the group's learned values, null meaning the usual one. <c>applied</c> is false for a
    /// change only proposed while the group was watching. Kept so the admins can see and undo it.
    /// </summary>
    internal sealed class DetectionAdjustmentEntity
    {
        public long AdjustmentId { get; set; }
        public long GroupId { get; set; }
        public string Kind { get; set; }
        public string Reason { get; set; }
        public int? FromValue { get; set; }
        public int? FromDays { get; set; }
        public int? ToValue { get; set; }
        public int? ToDays { get; set; }
        public long? AccountId { get; set; }
        public long? LabelId { get; set; }
        public bool Applied { get; set; }
        public DateTimeOffset? UndoneAt { get; set; }
        public long? UndoneByAccountId { get; set; }
        public string Edition { get; set; }
        public DateTimeOffset MadeAt { get; set; }
    }

    /// <summary>
    /// One copy of a Detection report or a filter's end-of-trial notice, sent to an admin in private or
    /// to the group's admin chat (design section 6). A report is the group, the member and when it was
    /// opened (<c>opened_at</c>); a notice the group, the rule and when it was sent. The first decision
    /// is written on every copy, so whichever is pressed later shows it and every copy is edited to say
    /// it.
    /// </summary>
    internal sealed class ReportCopyEntity
    {
        public long CopyId { get; set; }
        public long GroupId { get; set; }
        public string Kind { get; set; }
        public long? AccountId { get; set; }
        public long? FilterId { get; set; }
        public long? AdjustmentId { get; set; }
        public DateTimeOffset OpenedAt { get; set; }
        public long ChatId { get; set; }
        public long MessageId { get; set; }
        public string Edition { get; set; }
        public DateTimeOffset SentAt { get; set; }
        public string Decision { get; set; }
        public long? DecidedByAccountId { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
    }

    /// <summary>
    /// One sign that fired for a member (Detection v2, design section 12), with the member's state at
    /// that moment and what was done, or would have been done in observe mode. Never message text: the
    /// message's type, a hash of its text and the file's unique id. <c>outcome</c> is what an admin (or
    /// the bot) then decided about the member, from <c>member_labels</c> (<c>label_id</c>): the
    /// training data.
    /// </summary>
    internal sealed class DetectionEntity
    {
        public long DetectionId { get; set; }
        public long GroupId { get; set; }
        public long? AccountId { get; set; }
        public string Sign { get; set; }
        public string Phase { get; set; }
        public int Weight { get; set; }
        public int ScoreBefore { get; set; }
        public int ScoreAfter { get; set; }
        public string Action { get; set; }
        public bool Deleted { get; set; }
        public bool Reported { get; set; }
        public string Mode { get; set; }
        public long? MessageId { get; set; }
        public string MessageType { get; set; }
        public string TextHash { get; set; }
        public string MediaUniqueId { get; set; }
        public string TrustLevel { get; set; }
        public long? MemberMessages { get; set; }
        public DateTimeOffset? MemberSince { get; set; }
        public string Evidence { get; set; }
        public string Edition { get; set; }
        public DateTimeOffset DetectedAt { get; set; }
        public string Outcome { get; set; }
        public long? LabelId { get; set; }
        public long? DecidedByAccountId { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }

        /// <summary>abuse or group (step 7c): what the sign is about, as the group's settings said then.</summary>
        public string Category { get; set; }
    }

    /// <summary>
    /// A decision about a member: a ban, kick, mute, warn or delete, or its undoing, with who took it
    /// and how (<c>source</c>: command, button, report, app for the Telegram app itself, auto for the
    /// bot's own checks). It is what Detection learns a group's rules from. <c>missed</c> is an
    /// admin's decision no sign fired before; <c>undone_at</c> is set when it was taken back.
    /// <c>label_key</c> makes a decision both editions saw one row.
    /// </summary>
    internal sealed class MemberLabelEntity
    {
        public long LabelId { get; set; }
        public long GroupId { get; set; }
        public long? AccountId { get; set; }
        public string Label { get; set; }
        public string Action { get; set; }
        public string Source { get; set; }
        public long? DecidedByAccountId { get; set; }
        public long? MessageId { get; set; }
        public string MessageType { get; set; }
        public string TextHash { get; set; }
        public string MediaUniqueId { get; set; }
        public string Reason { get; set; }
        public bool? Missed { get; set; }
        public DateTimeOffset? UndoneAt { get; set; }
        public long? UndoneByLabelId { get; set; }
        public string LabelKey { get; set; }
        public string Edition { get; set; }
        public DateTimeOffset DecidedAt { get; set; }

        /// <summary>
        /// abuse, group, or null when not known (step 7c): from the reason the admin gave, else the signs seen in
        /// the day before, a warn being a group rule. Only abuse says anything about trust.
        /// </summary>
        public string Category { get; set; }
    }

    /// <summary>
    /// What Detection's profile lookup (getChat on a member, design section 7, build step 11) found about an
    /// account: flags only (<c>bio_link</c>, <c>bio_mention</c>, <c>personal_channel</c>, ...), never the bio text,
    /// and the id of their personal channel. One row per account, updated at each lookup (at most daily). The
    /// account id is nullable so the row can be anonymised in place (design section 12).
    /// </summary>
    internal sealed class AccountProfileFlagsEntity
    {
        public long ProfileFlagsId { get; set; }
        public long? AccountId { get; set; }
        public string[] Flags { get; set; }
        public long? PersonalChannelId { get; set; }
        public string BioScript { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    /// <summary>
    /// A public channel or group a member pointed at (their bio, personal channel, a forward, a mention or a link),
    /// as getChat showed it (build step 11): its public id and username, flags about its description, and its
    /// member count. Never its title or description text. Shared by every group; looked up at most once a day.
    /// </summary>
    internal sealed class ChannelProfileEntity
    {
        public long TelegramChatId { get; set; }
        public string ChatType { get; set; }
        public string Username { get; set; }
        public string[] Flags { get; set; }
        public int? MemberCount { get; set; }
        public long? LinkedChatId { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    /// <summary>
    /// A file an admin banned in a group (design section 3, build step 12): by banning or kicking someone with a reply to
    /// it, with Ban on a Detection report about it, or with /banmedia. Posting it again (the same <c>file_unique_id</c>)
    /// is the <c>banned_media</c> sign, which deletes it. Never the file itself. <c>pdq_hash</c> is for the near-copy
    /// check that comes with the ML step.
    /// </summary>
    internal sealed class BannedMediaEntity
    {
        public long GroupId { get; set; }
        public string MediaUniqueId { get; set; }
        public string MediaType { get; set; }
        public byte[] PdqHash { get; set; }
        public long? AddedByAccountId { get; set; }
        public string Source { get; set; }
        public DateTimeOffset AddedAt { get; set; }
    }
}
