using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>
    /// Reports in <c>reports</c> and the actions taken on them in <c>report_actions</c>. Group and
    /// member rows are created on first use by <see cref="PrincipalResolver"/>.
    /// </summary>
    internal sealed class PostgresReportHistoryRepository : IReportHistoryRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresReportHistoryRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        internal static string ActionValue(ReportAction action) => action switch
        {
            ReportAction.Deleted => "deleted",
            ReportAction.Warned => "warned",
            ReportAction.Kicked => "kicked",
            ReportAction.Banned => "banned",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        public async Task<long> RecordReportAsync(ReportEvent report)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // The same lock as the other histories and the observation batch, which also create accounts.
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, report.ChatId,
                chatType: PostgresBanHistoryRepository.ChatType(report.ChatId));
            PrincipalResolver.UserIds reporter = null;
            if (report.ReporterId.HasValue)
                reporter = await PrincipalResolver.EnsureUserAsync(connection, transaction, report.ReporterId.Value);
            PrincipalResolver.UserIds target = null;
            if (report.TargetUserId.HasValue)
                target = await PrincipalResolver.EnsureUserAsync(connection, transaction, report.TargetUserId.Value);

            var id = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
INSERT INTO reports (group_id, reporter_user_id, reporter_account_id, reporter_display, target_user_id,
    target_account_id, target_display, report_message_id, target_message_id, status, reported_at)
VALUES (@GroupId, @ReporterUserId, @ReporterAccountId, @ReporterDisplay, @TargetUserId,
    @TargetAccountId, @TargetDisplay, @ReportMessageId, @TargetMessageId, 'open', @At)
ON CONFLICT (group_id, target_message_id) WHERE status = 'open' AND target_message_id IS NOT NULL DO NOTHING
RETURNING report_id",
                new
                {
                    group.GroupId, ReporterUserId = reporter?.UserId, ReporterAccountId = reporter?.AccountId,
                    report.ReporterDisplay, TargetUserId = target?.UserId, TargetAccountId = target?.AccountId,
                    report.TargetDisplay, report.ReportMessageId, report.TargetMessageId, report.At
                }, transaction));
            id ??= await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
SELECT report_id FROM reports
WHERE group_id = @GroupId AND target_message_id = @TargetMessageId AND status = 'open'",
                new { group.GroupId, report.TargetMessageId }, transaction));
            await transaction.CommitAsync();
            return id.Value;
        }

        public async Task<long?> FindAsync(ReportLookup lookup)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, lookup.ChatId);
            if (groupId == null) return null;

            if (lookup.ReportMessageId.HasValue)
                return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
SELECT report_id FROM reports WHERE group_id = @GroupId AND report_message_id = @ReportMessageId
ORDER BY report_id DESC LIMIT 1", new { GroupId = groupId, lookup.ReportMessageId }));

            if (lookup.TargetUserId.HasValue)
                return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
SELECT r.report_id FROM reports r JOIN telegram_accounts a ON a.account_id = r.target_account_id
WHERE r.group_id = @GroupId AND a.telegram_user_id = @TargetUserId AND r.status = 'open'
ORDER BY r.reported_at DESC, r.report_id DESC LIMIT 1", new { GroupId = groupId, lookup.TargetUserId }));

            if (lookup.TargetMessageId.HasValue)
                // A delete button names the reported message, or the report message itself when the
                // reported one could not be forwarded.
                return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
SELECT report_id FROM reports
WHERE group_id = @GroupId AND (target_message_id = @TargetMessageId OR report_message_id = @TargetMessageId)
ORDER BY (status = 'open') DESC, report_id DESC LIMIT 1", new { GroupId = groupId, lookup.TargetMessageId }));

            return null;
        }

        public async Task RecordActionAsync(long reportId, ReportAction action, long? actorId, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            long? actorUserId = null;
            if (actorId.HasValue)
            {
                await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                    new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
                actorUserId = (await PrincipalResolver.EnsureUserAsync(connection, transaction, actorId.Value)).UserId;
            }
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO report_actions (report_id, actor_user_id, action, occurred_at)
VALUES (@ReportId, @ActorUserId, @Action, @At)",
                new { ReportId = reportId, ActorUserId = actorUserId, Action = ActionValue(action), At = at }, transaction));
            await transaction.CommitAsync();
        }

        public async Task<bool> RecordSolvedAsync(long chatId, long reportMessageId, long solverId, string solverDisplay,
            DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, chatId, transaction);
            if (groupId == null) return false;
            var reportId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
SELECT report_id FROM reports
WHERE group_id = @GroupId AND report_message_id = @ReportMessageId AND status = 'open'
ORDER BY report_id DESC LIMIT 1 FOR UPDATE",
                new { GroupId = groupId, ReportMessageId = reportMessageId }, transaction));
            if (reportId == null) return false;

            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var solver = await PrincipalResolver.EnsureUserAsync(connection, transaction, solverId);
            // GREATEST keeps resolved_at >= reported_at if the two editions' clocks disagree.
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE reports SET status = 'resolved', resolution_action = 'solved', solver_user_id = @SolverUserId,
    solver_display = @SolverDisplay, resolved_at = GREATEST(@At, reported_at)
WHERE report_id = @ReportId",
                new { ReportId = reportId, SolverUserId = solver.UserId, SolverDisplay = solverDisplay, At = at },
                transaction));
            await transaction.CommitAsync();
            return true;
        }

        public async Task<IReadOnlyList<ReportRecord>> GetReportsAsync(long chatId, int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = (await connection.QueryAsync<Row>(new CommandDefinition(@"
SELECT r.report_id AS ReportId, r.report_message_id AS ReportMessageId, reporter.telegram_user_id AS ReporterId,
       target.telegram_user_id AS TargetId, r.target_message_id AS TargetMessageId, r.status AS Status,
       r.resolution_action AS ResolutionAction, solver.telegram_user_id AS SolverId, r.solver_display AS SolverDisplay,
       r.reported_at AS ReportedAt, r.resolved_at AS ResolvedAt
FROM reports r
JOIN groups g ON g.group_id = r.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
LEFT JOIN telegram_accounts reporter ON reporter.account_id = r.reporter_account_id
LEFT JOIN telegram_accounts target ON target.account_id = r.target_account_id
LEFT JOIN user_identity_links sl ON sl.user_id = r.solver_user_id AND sl.unlinked_at IS NULL
LEFT JOIN telegram_accounts solver ON solver.account_id = sl.account_id
WHERE c.telegram_chat_id = @ChatId
ORDER BY r.reported_at DESC, r.report_id DESC
LIMIT @Limit", new { ChatId = chatId, Limit = limit }))).ToList();
            if (rows.Count == 0) return Array.Empty<ReportRecord>();

            var actions = (await connection.QueryAsync<ActionRow>(new CommandDefinition(@"
SELECT ra.report_id AS ReportId, ra.action AS Action, actor.telegram_user_id AS ActorId
FROM report_actions ra
LEFT JOIN user_identity_links l ON l.user_id = ra.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id
WHERE ra.report_id = ANY(@Ids)
ORDER BY ra.occurred_at, ra.report_action_id", new { Ids = rows.Select(r => r.ReportId).ToArray() })))
                .ToLookup(a => a.ReportId);

            return rows.Select(r => new ReportRecord(r.ReportId, r.ReportMessageId, r.ReporterId, r.TargetId,
                r.TargetMessageId, r.Status, r.ResolutionAction, r.SolverId, r.SolverDisplay, ToUtc(r.ReportedAt),
                r.ResolvedAt.HasValue ? ToUtc(r.ResolvedAt.Value) : null,
                actions[r.ReportId].Select(a => (a.Action, a.ActorId)).ToList())).ToList();
        }

        private static DateTimeOffset ToUtc(DateTime value) =>
            new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        private sealed class Row
        {
            public long ReportId { get; set; }
            public long ReportMessageId { get; set; }
            public long? ReporterId { get; set; }
            public long? TargetId { get; set; }
            public long? TargetMessageId { get; set; }
            public string Status { get; set; }
            public string ResolutionAction { get; set; }
            public long? SolverId { get; set; }
            public string SolverDisplay { get; set; }
            public DateTime ReportedAt { get; set; }
            public DateTime? ResolvedAt { get; set; }
        }

        private sealed class ActionRow
        {
            public long ReportId { get; set; }
            public string Action { get; set; }
            public long? ActorId { get; set; }
        }
    }
}
