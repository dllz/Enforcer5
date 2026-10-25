using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>The ban kinds the history records. Kicks, mutes and global bans come later.</summary>
    internal enum BanKind
    {
        Ban,
        Tempban
    }

    /// <summary>How a ban ended.</summary>
    internal enum BanEnd
    {
        /// <summary>Lifted by an admin (or by Enforcer on an admin's behalf). Stored as <c>revoked</c>.</summary>
        Unbanned,

        /// <summary>A tempban that ran out. Stored as <c>completed</c>.</summary>
        Expired
    }

    /// <summary>
    /// A ban Enforcer made. <paramref name="ActorId"/> is the Telegram id of the admin who asked
    /// for it, or null when Enforcer acted on its own (flood, max warns, a global ban, ...).
    /// <paramref name="TriggerWarningEventId"/> is the warn that reached the maximum, for a
    /// max-warns ban.
    /// </summary>
    internal sealed record BanEvent(long ChatId, long UserId, BanKind Kind, string Reason, long? ActorId,
        DateTimeOffset At, DateTimeOffset? ExpiresAt, string Edition, long? TriggerWarningEventId = null);

    /// <summary>A ban being lifted.</summary>
    internal sealed record UnbanEvent(long ChatId, long UserId, BanEnd End, long? ActorId, DateTimeOffset At);

    /// <summary>One ban from the history, newest first when listed.</summary>
    internal sealed record BanRecord(string Kind, string Status, string Reason, long? ActorId,
        DateTimeOffset ImposedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? ResolvedAt, string Edition);

    /// <summary>
    /// Ban and unban history per group and member. Writes are history only: Telegram and Redis stay
    /// what enforcement runs on, so callers never wait on this (see <see cref="Helpers.BanHistory"/>).
    /// </summary>
    internal interface IBanHistoryRepository
    {
        /// <summary>
        /// Records a ban. Telegram holds one ban per member, so any ban still open for this member
        /// of this group is closed as superseded (<c>revoked</c>) first.
        /// </summary>
        Task RecordBanAsync(BanEvent ban);

        /// <summary>
        /// Closes the member's open ban. Returns false when there was none, e.g. a ban from before
        /// the history started; nothing is written then.
        /// </summary>
        Task<bool> RecordUnbanAsync(UnbanEvent unban);

        /// <summary>The member's bans in this group, newest first.</summary>
        Task<IReadOnlyList<BanRecord>> GetHistoryAsync(long chatId, long userId, int limit);
    }
}
