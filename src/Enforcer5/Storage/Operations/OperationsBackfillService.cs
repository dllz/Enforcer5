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
        internal int ReconciliationMismatches { get; private set; }

        internal OperationsBackfillService(PostgresDatabase database)
        {
            _database = database;
            _sanctions = new SanctionRepository(database);
            _globalSanctions = new GlobalSanctionRepository(database);
            _reports = new ReportRepository(database);
            _statistics = new StatisticsRepository(database);
            _auditEvents = new AuditEventRepository(database);
        }

        internal async Task<OperationsBackfillResult> RunRedisPrimaryAsync(IEnumerable<long> knownGroupIds,
            IEnumerable<long> knownUserIds, IReadOnlyDictionary<long, IReadOnlyCollection<long>> reportMessageIds,
            bool reconcile = false,
            CancellationToken cancellationToken = default)
        {
            if (StorageModeProvider.Current != StorageMode.RedisPrimaryPostgresShadow)
                throw new InvalidOperationException("Operations backfill requires RedisPrimaryPostgresShadow mode.");
            var snapshot = await _reader.ReadAsync(knownGroupIds, knownUserIds, reportMessageIds, cancellationToken);
            var result = new OperationsBackfillResult { Errors = snapshot.Errors.ToArray() };
            if (reconcile)
                ReconciliationMismatches = await ReconcileAsync(
                    _database, knownGroupIds, knownUserIds, reportMessageIds,
                    snapshot, result.Errors, cancellationToken);
            foreach (var value in snapshot.Sanctions) { await _sanctions.UpsertAsync(value, cancellationToken); result.SanctionsWritten++; }
            foreach (var value in snapshot.GlobalSanctions) { await _globalSanctions.UpsertAsync(value, cancellationToken); result.GlobalSanctionsWritten++; }

            var reportIds = new Dictionary<long, long>();
            foreach (var value in snapshot.Reports)
            {
                reportIds[value.Id] = await _reports.UpsertAsync(value with { Id = 0 }, cancellationToken);
                result.ReportsWritten++;
            }
            foreach (var value in snapshot.ReportDeliveries)
            {
                await _reports.UpsertDeliveryAsync(value with { ReportId = reportIds[value.ReportId] }, cancellationToken);
                result.ReportDeliveriesWritten++;
            }
            foreach (var value in snapshot.UserStatistics) { await _statistics.UpsertAsync(value, cancellationToken); result.UserStatisticsWritten++; }
            foreach (var value in snapshot.GroupUserStatistics) { await _statistics.UpsertAsync(value, cancellationToken); result.GroupUserStatisticsWritten++; }
            foreach (var value in snapshot.BotStatisticBaselines) { await _statistics.UpsertAsync(value, cancellationToken); result.BotStatisticBaselinesWritten++; }
            foreach (var value in snapshot.AuditEvents) { await _auditEvents.AppendAsync(value, cancellationToken); result.AuditEventsWritten++; }
            return result;
        }

        internal static async Task<int> ReconcileAsync(PostgresDatabase database,
            IEnumerable<long> knownGroupIds, IEnumerable<long> knownUserIds,
            IReadOnlyDictionary<long, IReadOnlyCollection<long>> reportMessageIds,
            OperationsBackfillSnapshot snapshot, IReadOnlyList<Exception> errors,
            CancellationToken cancellationToken = default)
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
SELECT id AS Id, legacy_key AS LegacyKey FROM sanctions
WHERE status IN ('active', 'due') AND legacy_key LIKE 'redis:%'", transaction: transaction,
                cancellationToken: cancellationToken));
            foreach (var value in sanctions.Where(x => !sanctionKeys.Contains(x.LegacyKey)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE sanctions SET status = 'revoked',
resolved_at = now(), claimed_at = NULL, claimed_by = NULL WHERE id = @Id AND status IN ('active', 'due')",
                    new { value.Id }, transaction, cancellationToken: cancellationToken));
            }
            var globals = await connection.QueryAsync<long>(new CommandDefinition(@"SELECT user_id
FROM global_sanctions WHERE active AND legacy_key LIKE 'redis:globalBan:%'", transaction: transaction,
                cancellationToken: cancellationToken));
            foreach (var userId in globals.Where(id => !globalUsers.Contains(id)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE global_sanctions SET active = FALSE,
revocation_reason = 'Absent from Redis reconciliation snapshot', revoked_at = now()
WHERE user_id = @UserId AND active", new { UserId = userId }, transaction,
                    cancellationToken: cancellationToken));
            }
            if (groupIds.Length > 0)
            {
                var reports = await connection.QueryAsync<(long GroupId, long ReportMessageId)>(
                    new CommandDefinition(@"SELECT group_id AS GroupId, report_message_id AS ReportMessageId
FROM reports WHERE group_id = ANY(@GroupIds) AND status = 'open' AND legacy_key LIKE 'redis:%'",
                        new { GroupIds = groupIds }, transaction, cancellationToken: cancellationToken));
                foreach (var value in reports.Where(x => !reportKeys.Contains(x)))
                {
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"UPDATE reports SET status = 'dismissed',
solved_at = now() WHERE group_id = @GroupId AND report_message_id = @ReportMessageId AND status = 'open'",
                        value, transaction, cancellationToken: cancellationToken));
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
            foreach (var value in snapshot.AuditEvents.Where(x => x.LegacyKey != null))
                mismatches += await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM audit_events WHERE legacy_key = @LegacyKey) THEN 0 ELSE 1 END",
                    new { value.LegacyKey }, transaction, cancellationToken: cancellationToken));
            return mismatches;
        }
    }
}
