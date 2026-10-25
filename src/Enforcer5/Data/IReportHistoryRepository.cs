using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>What an admin did from a report's buttons. Several may be taken on one report.</summary>
    internal enum ReportAction
    {
        Deleted,
        Warned,
        Kicked,
        Banned
    }

    /// <summary>
    /// A report Enforcer passed on to the admins. <paramref name="ReportMessageId"/> is the /report or
    /// @admin message; <paramref name="TargetUserId"/> and <paramref name="TargetMessageId"/> are the
    /// message it replied to and its sender, null when the report replied to nothing. A sender who is
    /// one of Telegram's shared placeholders (a channel post, an anonymous admin) is not a person and
    /// is left null, with the chat's title as the display.
    /// </summary>
    internal sealed record ReportEvent(long ChatId, long ReportMessageId, long? ReporterId, string ReporterDisplay,
        long? TargetUserId, string TargetDisplay, long? TargetMessageId, DateTimeOffset At);

    /// <summary>
    /// How a button or command names a report: by its message when it knows it, else by the member
    /// it reported (the newest open report against them), else by the reported message.
    /// </summary>
    internal sealed record ReportLookup(long ChatId, long? ReportMessageId, long? TargetUserId, long? TargetMessageId);

    /// <summary>One report from the history, with the actions taken on it, oldest first.</summary>
    internal sealed record ReportRecord(long ReportId, long ReportMessageId, long? ReporterId, long? TargetId,
        long? TargetMessageId, string Status, string ResolutionAction, long? SolverId, string SolverDisplay,
        DateTimeOffset ReportedAt, DateTimeOffset? ResolvedAt, IReadOnlyList<(string Action, long? ActorId)> Actions);

    /// <summary>
    /// Report history: reports, the actions taken on them and who solved them. Writes only: Redis
    /// stays what the bot reads (see <see cref="Helpers.ReportHistory"/>).
    /// </summary>
    internal interface IReportHistoryRepository
    {
        /// <summary>
        /// Records a new report and returns its id. A second open report on the same message (which
        /// Redis already refuses) returns the open one instead.
        /// </summary>
        Task<long> RecordReportAsync(ReportEvent report);

        /// <summary>The report <paramref name="lookup"/> names, or null when the history has none.</summary>
        Task<long?> FindAsync(ReportLookup lookup);

        /// <summary>Records an action taken on the report.</summary>
        Task RecordActionAsync(long reportId, ReportAction action, long? actorId, DateTimeOffset at);

        /// <summary>
        /// Marks the report made by message <paramref name="reportMessageId"/> solved. Returns false
        /// when there is no open one, e.g. a report from before the history started.
        /// </summary>
        Task<bool> RecordSolvedAsync(long chatId, long reportMessageId, long solverId, string solverDisplay,
            DateTimeOffset at);

        /// <summary>The group's reports, newest first.</summary>
        Task<IReadOnlyList<ReportRecord>> GetReportsAsync(long chatId, int limit);
    }
}
