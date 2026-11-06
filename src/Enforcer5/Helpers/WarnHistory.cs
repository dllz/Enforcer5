using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Models;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The id of a warn event, so a max-warns ban can point at the warn that caused it. Known as soon as the warn is
    /// made; null when the warn changed nothing or was queued while PostgreSQL was down.
    /// </summary>
    internal sealed class WarningRef
    {
        internal long? EventId { get; set; }
    }

    /// <summary>
    /// Makes warn changes through <see cref="Repositories.Warns"/> (which writes the count and its history event
    /// together, rollout step 12) and tells Detection about the ones that are decisions about a member.
    /// </summary>
    internal static class WarnHistory
    {
        /// <summary>A change of <paramref name="kind"/>, as asked for by <paramref name="actorId"/> (null: the bot).</summary>
        internal static WarnOp Op(long chatId, long userId, WarnKind kind, WarnOperation operation, long quantity,
            long? actorId, string reason, ReportRef report = null) =>
            new(chatId, userId, kind, operation, quantity)
            {
                ActorId = actorId,
                Reason = HistoryWriter.Trim(reason),
                ReportId = report?.ReportId
            };

        /// <summary>Makes the change, and labels it for Detection.</summary>
        internal static async Task<WarnChanged> ApplyAsync(IWarnRepository warns, WarnOp op, bool fromReport = false)
        {
            var result = await warns.ApplyAsync(op);
            Label(op, result.Before, result.After, fromReport);
            return result;
        }

        /// <summary>Synchronous <see cref="ApplyAsync"/>, for the warn actions.</summary>
        internal static WarnChanged Apply(WarnOp op, ReportRef report = null) =>
            ApplyAsync(Repositories.Warns, op, report != null).GetAwaiter().GetResult();

        /// <summary>
        /// Warns are decisions Detection learns from; prewarns and media strikes are not. A warn added is a sanction,
        /// one an admin takes off is an undo.
        /// </summary>
        internal static void Label(WarnOp op, long before, long after, bool fromReport = false)
        {
            if (op.Kind != WarnKind.Warn || after == before) return;
            if (after > before)
                Detection.Decisions.Sanction(op.ChatId, op.UserId, "warn", op.ActorId, op.Reason, fromReport);
            else if (op.ActorId != null)
                Detection.Decisions.Undo(op.ChatId, op.UserId, "unwarn", op.ActorId);
        }
    }
}
