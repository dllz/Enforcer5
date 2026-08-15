using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Enforcer5.Storage.Operations
{
    internal sealed class OperationsBackfillService
    {
        private readonly LegacyOperationsReader _reader = new();
        private readonly PostgresDatabase _database;
        private readonly SanctionRepository _sanctions;
        private readonly GlobalSanctionRepository _globalSanctions;
        private readonly ReportRepository _reports;
        private readonly StatisticsRepository _statistics;
        private readonly AuditEventRepository _auditEvents;
        private readonly AccountModerationCounterRepository _moderationCounters;
        internal int ReconciliationMismatches { get; private set; }

        internal OperationsBackfillService(PostgresDatabase database)
        {
            _database = database;
            _sanctions = new SanctionRepository(database);
            _globalSanctions = new GlobalSanctionRepository(database);
            _reports = new ReportRepository(database);
            _statistics = new StatisticsRepository(database);
            _moderationCounters = new AccountModerationCounterRepository(database);
            _auditEvents = new AuditEventRepository(database);
        }

        internal async Task<OperationsBackfillResult> RunRedisPrimaryAsync(IEnumerable<long> knownGroupIds,
            IEnumerable<long> knownUserIds, IReadOnlyDictionary<long, IReadOnlyCollection<long>> reportMessageIds,
            bool reconcile = false,
            CancellationToken cancellationToken = default, bool includeGlobalData = true)
        {
            if (StorageModeProvider.Current != StorageMode.RedisPrimaryPostgresShadow)
                throw new InvalidOperationException("Operations backfill requires RedisPrimaryPostgresShadow mode.");
            var snapshot = await _reader.ReadAsync(knownGroupIds, knownUserIds, reportMessageIds,
                cancellationToken, includeGlobalData);
            var result = new OperationsBackfillResult { Errors = snapshot.Errors.ToArray() };
            if (reconcile)
                ReconciliationMismatches = await ReconcileAsync(
                    _database, knownGroupIds, knownUserIds, reportMessageIds,
                    snapshot, result.Errors, cancellationToken, includeGlobalData);
            foreach (var value in snapshot.Sanctions) { await _sanctions.UpsertAsync(value, cancellationToken); result.SanctionsWritten++; }
            foreach (var value in snapshot.GlobalSanctions) { await _globalSanctions.UpsertAsync(value, cancellationToken); result.GlobalSanctionsWritten++; }

            foreach (var value in snapshot.Reports)
            {
                await _reports.UpsertAsync(value with { Id = 0 }, cancellationToken);
                result.ReportsWritten++;
            }
            foreach (var value in snapshot.UserStatistics) { await _statistics.UpsertAsync(value, cancellationToken); result.UserStatisticsWritten++; }
            foreach (var value in snapshot.GroupUserStatistics) { await _statistics.UpsertAsync(value, cancellationToken); result.GroupUserStatisticsWritten++; }
            // Absolute values, so a re-run converges rather than double-counting.
            foreach (var value in snapshot.ModerationCounters)
            {
                await _moderationCounters.SetAsync(value.UserId, value.Kind, value.Count, cancellationToken);
                result.ModerationCountersWritten++;
            }
            foreach (var value in snapshot.BotStatisticBaselines) { await _statistics.UpsertAsync(value, cancellationToken); result.BotStatisticBaselinesWritten++; }
            foreach (var value in snapshot.AuditEvents.Where(x => x.GroupId.HasValue))
            {
                await _auditEvents.AppendAsync(value, cancellationToken);
                result.AuditEventsWritten++;
            }
            return result;
        }

        internal static async Task<int> ReconcileAsync(PostgresDatabase database,
            IEnumerable<long> knownGroupIds, IEnumerable<long> knownUserIds,
            IReadOnlyDictionary<long, IReadOnlyCollection<long>> reportMessageIds,
            OperationsBackfillSnapshot snapshot, IReadOnlyList<Exception> errors,
            CancellationToken cancellationToken = default, bool includeGlobalData = true)
        {
            if (errors.Count > 0)
            {
                Console.Error.WriteLine(
                    $"Operations reconciliation skipped removals: Redis snapshot has {errors.Count} error(s).");
                return 0;
            }

            var groupIds = knownGroupIds.Distinct().ToArray();
            var sanctionKeys = snapshot.Sanctions.Select(x => x.LegacyKey).Where(x => x != null).ToHashSet();
            var globalUsers = snapshot.GlobalSanctions.Select(x => x.UserId).ToHashSet();
            var reportKeys = snapshot.Reports.Select(x => (x.GroupId, x.ReportMessageId)).ToHashSet();

            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var mismatches = 0;
            var sanctions = await connection.QueryAsync<(long Id, string LegacyKey)>(new CommandDefinition(@"
SELECT sanction_id AS Id, legacy_key AS LegacyKey FROM sanctions
WHERE group_id = ANY(SELECT g.group_id FROM groups g JOIN telegram_chats tc ON tc.chat_id = g.chat_id
    WHERE tc.telegram_chat_id = ANY(@GroupIds))
  AND status IN ('active', 'due') AND legacy_key LIKE 'redis:%'", new { GroupIds = groupIds }, transaction,
                 cancellationToken: cancellationToken));
            foreach (var value in sanctions.Where(x => !sanctionKeys.Contains(x.LegacyKey)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE sanctions SET status = 'revoked',
resolved_at = now(), claimed_at = NULL, claimed_by = NULL WHERE sanction_id = @Id AND status IN ('active', 'due')",
                    new { value.Id }, transaction, cancellationToken: cancellationToken));
            }
            if (includeGlobalData)
            {
                var globals = await connection.QueryAsync<(long AccountId, long UserId)>(new CommandDefinition(@"
SELECT gs.account_id AS AccountId, ta.telegram_user_id AS UserId FROM global_sanctions gs
JOIN telegram_accounts ta ON ta.account_id = gs.account_id
WHERE gs.status = 'active' AND gs.legacy_key LIKE 'redis:globalBan:%'", transaction: transaction,
                    cancellationToken: cancellationToken));
                foreach (var value in globals.Where(x => !globalUsers.Contains(x.UserId)))
                {
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE global_sanctions SET status = 'revoked', resolved_at = now()
WHERE account_id = @AccountId AND status = 'active'", new { value.AccountId }, transaction,
                        cancellationToken: cancellationToken));
                }
            }
            if (groupIds.Length > 0)
            {
                var reports = await connection.QueryAsync<(long GroupId, long ReportMessageId)>(
                    new CommandDefinition(@"SELECT tc.telegram_chat_id AS GroupId, r.report_message_id AS ReportMessageId
FROM reports r JOIN groups g ON g.group_id = r.group_id JOIN telegram_chats tc ON tc.chat_id = g.chat_id
WHERE tc.telegram_chat_id = ANY(@GroupIds) AND status = 'open' AND legacy_key LIKE 'redis:%'",
                        new { GroupIds = groupIds }, transaction, cancellationToken: cancellationToken));
                foreach (var value in reports.Where(x => !reportKeys.Contains(x)))
                {
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"UPDATE reports r SET status = 'resolved',
resolution_action = 'dismissed', resolved_at = now() FROM groups g JOIN telegram_chats tc ON tc.chat_id = g.chat_id
WHERE r.group_id = g.group_id AND tc.telegram_chat_id = @GroupId
  AND r.report_message_id = @ReportMessageId AND r.status = 'open'", value, transaction,
                        cancellationToken: cancellationToken));
                }
            }

            // Statistics and audit events are durable history. Differences are reported, never deleted.
            mismatches += await CountHistoryMismatchesAsync(connection, transaction, snapshot, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Console.Error.WriteLine($"Operations reconciliation mismatches={mismatches}.");
            return mismatches;
        }

        private static async Task<int> CountHistoryMismatchesAsync(Npgsql.NpgsqlConnection connection,
            Npgsql.NpgsqlTransaction transaction, OperationsBackfillSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            var mismatches = 0;
            foreach (var value in snapshot.AuditEvents.Where(x => x.GroupId.HasValue && x.LegacyKey != null))
                mismatches += await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM group_audit_events WHERE legacy_key = @LegacyKey) THEN 0 ELSE 1 END",
                    new { value.LegacyKey }, transaction, cancellationToken: cancellationToken));
            return mismatches;
        }
    }
}
