using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>The sanction kinds the history records. Global bans come later.</summary>
    internal enum SanctionKind
    {
        Ban,
        Tempban,

        /// <summary>A ban lifted straight away: over as soon as it is made.</summary>
        Kick,

        Mute,
        Tempmute
    }

    /// <summary>How a ban or mute ended.</summary>
    internal enum BanEnd
    {
        /// <summary>Lifted by an admin (or by Enforcer on an admin's behalf). Stored as <c>revoked</c>.</summary>
        Unbanned,

        /// <summary>A tempban that ran out. Stored as <c>completed</c>.</summary>
        Expired
    }

    /// <summary>
    /// A ban, kick or mute Enforcer made. <paramref name="ActorId"/> is the Telegram id of the admin
    /// who asked for it, or null when Enforcer acted on its own (flood, max warns, a global ban, ...).
    /// <paramref name="TriggerWarningEventId"/> is the warn that reached the maximum, for a
    /// max-warns or media-limit sanction. <paramref name="ReportId"/> is the report it was taken
    /// from, for an action on a report.
    /// </summary>
    internal sealed record BanEvent(long ChatId, long UserId, SanctionKind Kind, string Reason, long? ActorId,
        DateTimeOffset At, DateTimeOffset? ExpiresAt, string Edition, long? TriggerWarningEventId = null,
        long? ReportId = null);

    /// <summary>A ban being lifted.</summary>
    internal sealed record UnbanEvent(long ChatId, long UserId, BanEnd End, long? ActorId, DateTimeOffset At);

    /// <summary>A mute being lifted by an admin.</summary>
    internal sealed record UnmuteEvent(long ChatId, long UserId, long? ActorId, DateTimeOffset At);

    /// <summary>One ban from the history, newest first when listed.</summary>
    internal sealed record BanRecord(string Kind, string Status, string Reason, long? ActorId,
        DateTimeOffset ImposedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? ResolvedAt, string Edition);

    /// <summary>
    /// Sanction history per group and member: bans, kicks and mutes, and their ends. Writes are
    /// history only: Telegram and Redis stay what enforcement runs on, so callers never wait on this
    /// (see <see cref="Helpers.BanHistory"/>).
    /// </summary>
    internal interface IBanHistoryRepository
    {
        /// <summary>
        /// Records a sanction. Telegram holds one ban and one restriction per member, so:
        /// a ban or a kick (which lifts any ban) closes the member's open ban as superseded
        /// (<c>revoked</c>); a mute closes the open mute, as <c>completed</c> at its expiry if it was
        /// a tempmute that had already run out, otherwise <c>revoked</c>. A kick's own row is closed
        /// (<c>completed</c>) at once.
        /// </summary>
        Task RecordBanAsync(BanEvent ban);

        /// <summary>
        /// Closes the member's open ban. Returns false when there was none, e.g. a ban from before
        /// the history started; nothing is written then.
        /// </summary>
        Task<bool> RecordUnbanAsync(UnbanEvent unban);

        /// <summary>
        /// Closes the member's open mute: <c>revoked</c>, or <c>completed</c> at its expiry for a
        /// tempmute that had already run out. Returns false when there was none.
        /// </summary>
        Task<bool> RecordUnmuteAsync(UnmuteEvent unmute);

        /// <summary>The member's bans in this group, newest first.</summary>
        Task<IReadOnlyList<BanRecord>> GetHistoryAsync(long chatId, long userId, int limit);

        /// <summary>The member's sanctions of every kind in this group, newest first.</summary>
        Task<IReadOnlyList<BanRecord>> GetSanctionsAsync(long chatId, long userId, int limit);
    }
}
