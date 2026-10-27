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
        /// this is written. <paramref name="report"/> is the report it was taken from, likewise.
        /// </summary>
        internal static void Banned(long chatId, long userId, SanctionKind kind, string reason, long? actorId,
            DateTime? expiresUtc = null, WarningRef trigger = null, ReportRef report = null, string key = null) =>
            Record(chatId, userId, kind, reason, actorId, expiresUtc, trigger, report, "ban", key, true);

        /// <summary>A kick Telegram has accepted; see <see cref="Banned"/>.</summary>
        internal static void Kicked(long chatId, long userId, string reason, long? actorId,
            WarningRef trigger = null, ReportRef report = null, string key = null) =>
            Record(chatId, userId, SanctionKind.Kick, reason, actorId, null, trigger, report, "kick", key, true);

        /// <summary>
        /// A mute Telegram has accepted, until <paramref name="untilUtc"/> for a tempmute; see
        /// <see cref="Banned"/>.
        /// </summary>
        /// <remarks>
        /// <paramref name="decision"/> false for a mute that says nothing about the member (Mute On
        /// Join mutes everyone who joins), which Detection must not learn from.
        /// </remarks>
        internal static void Muted(long chatId, long userId, string reason, long? actorId, DateTime? untilUtc = null,
            string key = null, bool decision = true) =>
            Record(chatId, userId, untilUtc.HasValue ? SanctionKind.Tempmute : SanctionKind.Mute, reason, actorId,
                untilUtc, null, null, "mute", key, decision);

        /// <summary>
        /// An unban Telegram has accepted, or a tempban that ran out. An admin's unban (or one made in
        /// the Telegram app, <paramref name="key"/>) is also a decision taken back, for Detection.
        /// </summary>
        internal static void Unbanned(long chatId, long userId, BanEnd end, long? actorId, string key = null)
        {
            var unban = new UnbanEvent(chatId, userId, end, actorId, DateTimeOffset.UtcNow);
            HistoryWriter.Enqueue(() => Repositories.BanHistory.RecordUnbanAsync(unban),
                $"Recording the unban of {userId} in {chatId}");
            if (end == BanEnd.Unbanned && (actorId != null || key != null))
                Detection.Decisions.Undo(chatId, userId, "unban", actorId, key);
        }

        /// <summary>An unmute Telegram has accepted; an admin's is a decision taken back, as for unbans.</summary>
        internal static void Unmuted(long chatId, long userId, long? actorId, string key = null)
        {
            var unmute = new UnmuteEvent(chatId, userId, actorId, DateTimeOffset.UtcNow);
            HistoryWriter.Enqueue(() => Repositories.BanHistory.RecordUnmuteAsync(unmute),
                $"Recording the unmute of {userId} in {chatId}");
            if (actorId != null || key != null)
                Detection.Decisions.Undo(chatId, userId, "unmute", actorId, key);
        }

        private static void Record(long chatId, long userId, SanctionKind kind, string reason, long? actorId,
            DateTime? expiresUtc, WarningRef trigger, ReportRef report, string what, string key, bool decision)
        {
            var ban = new BanEvent(chatId, userId, kind, HistoryWriter.Trim(reason), actorId, DateTimeOffset.UtcNow,
                expiresUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(expiresUtc.Value, DateTimeKind.Utc)) : null,
                Edition);
            HistoryWriter.Enqueue(
                () => Repositories.BanHistory.RecordBanAsync(ban with
                {
                    TriggerWarningEventId = trigger?.EventId,
                    ReportId = report?.ReportId,
                    Key = key
                }),
                $"Recording the {what} of {userId} in {chatId}");
            // And as a decision Detection learns from, written after the sanction.
            if (decision)
                Detection.Decisions.Sanction(chatId, userId, PostgresBanHistoryRepository.KindValue(kind), actorId, reason,
                    report != null, key);
        }
    }
}
