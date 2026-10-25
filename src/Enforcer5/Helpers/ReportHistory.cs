using System;
using Enforcer5.Data;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The id of a report in the history, once the history worker has found or written it. Sanctions
    /// and warns taken from the report are queued after it, so they read it once it is set.
    /// </summary>
    internal sealed class ReportRef
    {
        internal long? ReportId { get; set; }
    }

    /// <summary>
    /// Records reports, the actions admins take on them and their solving, through
    /// <see cref="HistoryWriter"/>: in order with bans and warns, in the background, never holding
    /// up the report itself. Redis stays what the bot reads.
    /// </summary>
    internal static class ReportHistory
    {
        /// <summary>
        /// A report passed on to the admins. <paramref name="report"/> is the /report or @admin
        /// message; the message it replied to, if any, is what was reported.
        /// </summary>
        internal static void Reported(Message report)
        {
            try
            {
                var target = report.ReplyToMessage;
                var reported = new ReportEvent(report.Chat.Id, report.MessageId,
                    Person(report), Display(report),
                    target == null ? null : Person(target), target == null ? null : Display(target), target?.MessageId,
                    DateTimeOffset.UtcNow);
                HistoryWriter.Enqueue(() => Repositories.ReportHistory.RecordReportAsync(reported),
                    $"Recording report {report.MessageId} in {report.Chat.Id}");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Recording report {report?.MessageId}");
            }
        }

        /// <summary>
        /// The report a button names, looked up on the history worker before anything queued after
        /// it, so pass the result to the sanction or warn the button makes.
        /// </summary>
        internal static ReportRef Find(long chatId, long? reportMessageId, long? targetUserId, long? targetMessageId)
        {
            var reference = new ReportRef();
            var lookup = new ReportLookup(chatId, reportMessageId, targetUserId, targetMessageId);
            HistoryWriter.Enqueue(async () => reference.ReportId = await Repositories.ReportHistory.FindAsync(lookup),
                $"Finding the report for an action in {chatId}");
            return reference;
        }

        /// <summary>An action taken on the report. Nothing is written for a report the history does not have.</summary>
        internal static void Acted(ReportRef report, ReportAction action, long? actorId)
        {
            if (report == null) return;
            var at = DateTimeOffset.UtcNow;
            HistoryWriter.Enqueue(async () =>
                {
                    if (report.ReportId.HasValue)
                        await Repositories.ReportHistory.RecordActionAsync(report.ReportId.Value, action, actorId, at);
                },
                $"Recording that a report was {action}");
        }

        /// <summary>The report made by message <paramref name="reportMessageId"/> marked solved by <paramref name="solver"/>.</summary>
        internal static void Solved(long chatId, long reportMessageId, User solver)
        {
            var at = DateTimeOffset.UtcNow;
            var display = Display(solver);
            HistoryWriter.Enqueue(() => Repositories.ReportHistory.RecordSolvedAsync(chatId, reportMessageId, solver.Id, display, at),
                $"Recording report {reportMessageId} in {chatId} as solved");
        }

        // Telegram's shared senders stand for a channel or an anonymous admin, never a person.
        private static long? Person(Message message) =>
            message.SenderChat != null || message.From == null || ObservationBuffer.Placeholders.Contains(message.From.Id)
                ? null
                : message.From.Id;

        private static string Display(Message message) =>
            Person(message).HasValue ? Display(message.From) : message.SenderChat?.Title ?? message.From?.FirstName;

        /// <summary>The name shown for a member at the time, as the design's *_display columns hold it.</summary>
        internal static string Display(User user)
        {
            var name = string.Join(" ", new[] { user.FirstName, user.LastName }).Trim();
            if (string.IsNullOrEmpty(name)) name = user.Id.ToString();
            return user.Username != null ? $"{name} (@{user.Username})" : name;
        }
    }
}
