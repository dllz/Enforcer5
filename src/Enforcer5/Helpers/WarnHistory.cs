using System;
using Enforcer5.Data;
using Enforcer5.Models;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The id of a warn event once it is written, so a max-warns ban can point at the warn that
    /// caused it. Set on the history worker, which writes the warn before the ban.
    /// </summary>
    internal sealed class WarningRef
    {
        internal long? EventId { get; set; }
    }

    /// <summary>
    /// Records warn changes in the warn history, through <see cref="HistoryWriter"/>: in order with
    /// the bans, in the background, never holding up the warn. Callers pass what Redis returned, so
    /// the balance in PostgreSQL is a copy of Redis's (see <see cref="WarnEvent"/>).
    /// </summary>
    internal static class WarnHistory
    {
        /// <summary>
        /// Warns added. Returns a reference to pass to <see cref="BanHistory.Banned"/> when the
        /// warn reached the maximum.
        /// </summary>
        internal static WarningRef Added(long chatId, long userId, WarnKind kind, long quantity, long activeAfter,
            long? lifetimeAfter, long? actorId, string reason = null) =>
            Record(chatId, userId, kind, WarnOperation.Add, quantity, activeAfter, lifetimeAfter, reason, actorId);

        /// <summary>Some warns taken off; <paramref name="quantity"/> is how many.</summary>
        internal static void Removed(long chatId, long userId, WarnKind kind, long quantity, long activeAfter,
            long? actorId, string reason) =>
            Record(chatId, userId, kind, WarnOperation.Remove, quantity, activeAfter, null, reason, actorId);

        /// <summary>The count set back to 0; <paramref name="quantity"/> is what it was.</summary>
        internal static void Reset(long chatId, long userId, WarnKind kind, long quantity, long? actorId, string reason) =>
            Record(chatId, userId, kind, WarnOperation.Reset, quantity, 0, null, reason, actorId);

        private static WarningRef Record(long chatId, long userId, WarnKind kind, WarnOperation operation, long quantity,
            long activeAfter, long? lifetimeAfter, string reason, long? actorId)
        {
            var reference = new WarningRef();
            // Nothing changed (a reset of no warns): the schema only holds real changes.
            if (quantity <= 0) return reference;
            var warn = new WarnEvent(chatId, userId, kind, operation, quantity, activeAfter, lifetimeAfter,
                HistoryWriter.Trim(reason), actorId, DateTimeOffset.UtcNow);
            HistoryWriter.Enqueue(async () => reference.EventId = await Repositories.WarningHistory.RecordAsync(warn),
                $"Recording a {operation} of {quantity} {kind} for {userId} in {chatId}");
            return reference;
        }
    }
}
