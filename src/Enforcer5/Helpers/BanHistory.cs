using System;
using Enforcer5.Data;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Records bans, kicks and mutes, and their ends, in the sanction history through
    /// <see cref="HistoryWriter"/>: in order, in the background, never holding up the action itself.
    /// </summary>
    internal static class BanHistory
    {
#if PREMIUM
        internal const string Edition = "premium";
#else
        internal const string Edition = "normal";
#endif

        /// <summary>
        /// A ban Telegram has accepted. <paramref name="trigger"/> is the warn that reached the
        /// group's maximum, for a max-warns ban; it was queued first, so its id is known by the time
        /// this is written.
        /// </summary>
        internal static void Banned(long chatId, long userId, SanctionKind kind, string reason, long? actorId,
            DateTime? expiresUtc = null, WarningRef trigger = null) =>
            Record(chatId, userId, kind, reason, actorId, expiresUtc, trigger, "ban");

        /// <summary>A kick Telegram has accepted; see <see cref="Banned"/>.</summary>
        internal static void Kicked(long chatId, long userId, string reason, long? actorId,
            WarningRef trigger = null) =>
            Record(chatId, userId, SanctionKind.Kick, reason, actorId, null, trigger, "kick");

        /// <summary>
        /// A mute Telegram has accepted, until <paramref name="untilUtc"/> for a tempmute; see
        /// <see cref="Banned"/>.
        /// </summary>
        internal static void Muted(long chatId, long userId, string reason, long? actorId, DateTime? untilUtc = null) =>
            Record(chatId, userId, untilUtc.HasValue ? SanctionKind.Tempmute : SanctionKind.Mute, reason, actorId,
                untilUtc, null, "mute");

        /// <summary>An unban Telegram has accepted, or a tempban that ran out.</summary>
        internal static void Unbanned(long chatId, long userId, BanEnd end, long? actorId)
        {
            var unban = new UnbanEvent(chatId, userId, end, actorId, DateTimeOffset.UtcNow);
            HistoryWriter.Enqueue(() => Repositories.BanHistory.RecordUnbanAsync(unban),
                $"Recording the unban of {userId} in {chatId}");
        }

        /// <summary>An unmute Telegram has accepted.</summary>
        internal static void Unmuted(long chatId, long userId, long? actorId)
        {
            var unmute = new UnmuteEvent(chatId, userId, actorId, DateTimeOffset.UtcNow);
            HistoryWriter.Enqueue(() => Repositories.BanHistory.RecordUnmuteAsync(unmute),
                $"Recording the unmute of {userId} in {chatId}");
        }

        private static void Record(long chatId, long userId, SanctionKind kind, string reason, long? actorId,
            DateTime? expiresUtc, WarningRef trigger, string what)
        {
            var ban = new BanEvent(chatId, userId, kind, HistoryWriter.Trim(reason), actorId, DateTimeOffset.UtcNow,
                expiresUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(expiresUtc.Value, DateTimeKind.Utc)) : null,
                Edition);
            HistoryWriter.Enqueue(
                () => Repositories.BanHistory.RecordBanAsync(ban with { TriggerWarningEventId = trigger?.EventId }),
                $"Recording the {what} of {userId} in {chatId}");
        }
    }
}
