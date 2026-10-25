using System;
using Enforcer5.Data;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Records bans and unbans in the ban history, through <see cref="HistoryWriter"/>: in order,
    /// in the background, never holding up the ban itself.
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
        internal static void Banned(long chatId, long userId, BanKind kind, string reason, long? actorId,
            DateTime? expiresUtc = null, WarningRef trigger = null)
        {
            var ban = new BanEvent(chatId, userId, kind, HistoryWriter.Trim(reason), actorId, DateTimeOffset.UtcNow,
                expiresUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(expiresUtc.Value, DateTimeKind.Utc)) : null,
                Edition);
            HistoryWriter.Enqueue(
                () => Repositories.BanHistory.RecordBanAsync(ban with { TriggerWarningEventId = trigger?.EventId }),
                $"Recording the ban of {userId} in {chatId}");
        }

        /// <summary>An unban Telegram has accepted, or a tempban that ran out.</summary>
        internal static void Unbanned(long chatId, long userId, BanEnd end, long? actorId)
        {
            var unban = new UnbanEvent(chatId, userId, end, actorId, DateTimeOffset.UtcNow);
            HistoryWriter.Enqueue(() => Repositories.BanHistory.RecordUnbanAsync(unban),
                $"Recording the unban of {userId} in {chatId}");
        }
    }
}
