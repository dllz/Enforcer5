using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage.Loading;
using Npgsql;

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
        internal ReconciliationOutcome ReconciliationOutcome { get; private set; }

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
            CancellationToken cancellationToken = default, bool includeGlobalData = true,
            MigrationRunTracker tracker = null)
        {
            if (StorageModeProvider.Current != StorageMode.RedisPrimaryPostgresShadow)
                throw new InvalidOperationException("Operations backfill requires RedisPrimaryPostgresShadow mode.");
            var snapshot = await _reader.ReadAsync(knownGroupIds, knownUserIds, reportMessageIds,
                cancellationToken, includeGlobalData);
            var result = new OperationsBackfillResult
            {
                Errors = snapshot.Errors.ToArray(),
                DataQualityIssues = snapshot.DataQualityIssues.ToArray()
            };
            if (reconcile)
            {
                ReconciliationOutcome = await ReconcileAsync(_database, knownGroupIds, knownUserIds,
                    reportMessageIds, snapshot, result.Errors, cancellationToken, includeGlobalData,
                    tracker);
                ReconciliationMismatches = ReconciliationOutcome.Total;
            }
            foreach (var value in snapshot.Sanctions) { await _sanctions.UpsertAsync(value, cancellationToken); result.SanctionsWritten++; }
            foreach (var value in snapshot.GlobalSanctions) { await _globalSanctions.UpsertAsync(value, cancellationToken); result.GlobalSanctionsWritten++; }

            foreach (var value in snapshot.Reports)
            {
                await _reports.UpsertAsync(value with { Id = 0 }, cancellationToken);
                result.ReportsWritten++;
            }
            // Last-seen timestamps go in as one statement. This replaced two per-row loops covering
            // 4,752,053 user totals and 7,383,778 per-group totals - roughly 12.1M transactions,
            // which is why this domain had never finished. The counts are owned by Storage/Loading.
            result.LastSeenApplied = await _statistics.ApplyLastSeenAsync(snapshot.GroupUserLastSeen,
                cancellationToken);
            // Absolute values, so a re-run converges rather than double-counting.
            foreach (var value in snapshot.ModerationCounters)
            {
                await _moderationCounters.SetAsync(value.UserId, value.Kind, value.Count, cancellationToken);
                result.ModerationCountersWritten++;
            }
            foreach (var value in snapshot.BotStatisticBaselines)
                if (await _statistics.UpsertAsync(value, cancellationToken)) result.BotStatisticBaselinesWritten++;
            foreach (var value in snapshot.AuditEvents.Where(x => x.GroupId.HasValue))
            {
                await _auditEvents.AppendAsync(value, cancellationToken);
                result.AuditEventsWritten++;
            }
            return result;
        }

        /// <summary>
        /// Compares the Redis snapshot against PostgreSQL in both directions.
        /// <para>
        /// Two things this had to stop doing. It returned zero the moment the snapshot carried any
        /// error, printing a count indistinguishable from a clean run - and since the snapshot
        /// always had errors at full scale, operations reconciliation had never actually compared
        /// anything. And it only ever looked for PostgreSQL rows missing from Redis, so a backfill
        /// that silently skipped records reconciled clean by construction.
        /// </para>
        /// <para>
        /// An incomplete snapshot now withholds <em>removals</em> - which is the part that would be
        /// unsafe - while still running every comparison and reporting honestly.
        /// </para>
        /// </summary>
        internal static async Task<ReconciliationOutcome> ReconcileAsync(PostgresDatabase database,
            IEnumerable<long> knownGroupIds, IEnumerable<long> knownUserIds,
            IReadOnlyDictionary<long, IReadOnlyCollection<long>> reportMessageIds,
            OperationsBackfillSnapshot snapshot, IReadOnlyList<Exception> errors,
            CancellationToken cancellationToken = default, bool includeGlobalData = true,
            MigrationRunTracker tracker = null)
        {
            var removalsSkipped = errors.Count > 0;
            if (removalsSkipped)
                Console.Error.WriteLine(
                    $"Operations reconciliation: Redis snapshot has {errors.Count} error(s), so orphan " +
                    "removal is withheld. Comparisons still run.");

            var groupIds = knownGroupIds.Distinct().ToArray();
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await ReconciliationStaging.StageKeysAsync(connection, transaction, "recon_sanctions",
                snapshot.Sanctions.Select(x => x.LegacyKey), cancellationToken);
            await ReconciliationStaging.StageKeysAsync(connection, transaction, "recon_audit",
                snapshot.AuditEvents.Where(x => x.GroupId.HasValue).Select(x => x.LegacyKey),
                cancellationToken);
            await ReconciliationStaging.StagePairsAsync(connection, transaction, "recon_reports",
                snapshot.Reports.Select(x => (x.GroupId, x.ReportMessageId)), cancellationToken);
            await ReconciliationStaging.StageKeyedValuesAsync(connection, transaction, "recon_counters",
                snapshot.ModerationCounters.Select(x => (x.UserId, x.Kind, x.Count)), cancellationToken);

            var outcome = new ReconciliationOutcome(0, 0, 0, removalsSkipped);
            outcome = outcome.Add(await ReconcileSanctionsAsync(connection, transaction, groupIds,
                removalsSkipped, cancellationToken));
            if (includeGlobalData)
                outcome = outcome.Add(await ReconcileGlobalSanctionsAsync(connection, transaction,
                    snapshot, removalsSkipped, cancellationToken));
            if (groupIds.Length > 0)
                outcome = outcome.Add(await ReconcileReportsAsync(connection, transaction, groupIds,
                    removalsSkipped, cancellationToken));
            outcome = outcome.Add(await ReconcileAuditAsync(connection, transaction, cancellationToken));
            outcome = outcome.Add(await ReconcileCountersAsync(connection, transaction, cancellationToken));

            if (tracker != null && outcome.Total > 0)
                await RecordMismatchesAsync(connection, transaction, tracker, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            Console.Error.WriteLine($"Operations reconciliation: {outcome}.");
            return outcome;
        }

        private static async Task<ReconciliationOutcome> ReconcileSanctionsAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, long[] groupIds, bool removalsSkipped,
            CancellationToken cancellationToken)
        {
            const string scoped = @"
FROM sanctions s
 WHERE s.group_id = ANY(SELECT g.group_id FROM groups g JOIN telegram_chats tc ON tc.chat_id = g.chat_id
                         WHERE tc.telegram_chat_id = ANY(@GroupIds))
   AND s.status IN ('active', 'due') AND s.legacy_key LIKE 'redis:%'
   AND NOT EXISTS (SELECT 1 FROM recon_sanctions k WHERE k.source_key = s.legacy_key)";

            var orphans = removalsSkipped
                ? await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT count(*)::int " + scoped, new { GroupIds = groupIds }, transaction,
                    cancellationToken: cancellationToken))
                : await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions SET status = 'revoked', resolved_at = now(), claimed_at = NULL, claimed_by = NULL
 WHERE sanction_id IN (SELECT s.sanction_id " + scoped + ")",
                    new { GroupIds = groupIds }, transaction, cancellationToken: cancellationToken));

            // The direction that did not exist: staged in Redis, absent from PostgreSQL.
            var missing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*)::int FROM recon_sanctions k
 WHERE k.source_key IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sanctions s WHERE s.legacy_key = k.source_key)",
                transaction: transaction, cancellationToken: cancellationToken));

            return new ReconciliationOutcome(orphans, missing, 0, removalsSkipped);
        }

        private static async Task<ReconciliationOutcome> ReconcileGlobalSanctionsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            OperationsBackfillSnapshot snapshot, bool removalsSkipped, CancellationToken cancellationToken)
        {
            var active = snapshot.GlobalSanctions.Where(x => x.Active).Select(x => x.UserId).ToArray();
            const string scoped = @"
FROM global_sanctions gs
 JOIN telegram_accounts ta ON ta.account_id = gs.account_id
 WHERE gs.status = 'active' AND gs.legacy_key LIKE 'redis:globalBan:%'
   AND NOT (ta.telegram_user_id = ANY(@Active))";

            var orphans = removalsSkipped
                ? await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT count(*)::int " + scoped, new { Active = active }, transaction,
                    cancellationToken: cancellationToken))
                : await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE global_sanctions SET status = 'revoked', resolved_at = now()
 WHERE global_sanction_id IN (SELECT gs.global_sanction_id " + scoped + ")",
                    new { Active = active }, transaction, cancellationToken: cancellationToken));

            // A global ban is the case where absence means allowed, so a record present in Redis and
            // missing from PostgreSQL is the most consequential miss in the whole migration.
            var missing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*)::int FROM unnest(@Active) AS redis(telegram_user_id)
 WHERE NOT EXISTS (SELECT 1 FROM global_sanctions gs
                     JOIN telegram_accounts ta ON ta.account_id = gs.account_id
                    WHERE ta.telegram_user_id = redis.telegram_user_id AND gs.status = 'active')",
                new { Active = active }, transaction, cancellationToken: cancellationToken));

            return new ReconciliationOutcome(orphans, missing, 0, removalsSkipped);
        }

        private static async Task<ReconciliationOutcome> ReconcileReportsAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, long[] groupIds, bool removalsSkipped,
            CancellationToken cancellationToken)
        {
            const string scoped = @"
FROM reports r
 JOIN groups g ON g.group_id = r.group_id
 JOIN telegram_chats tc ON tc.chat_id = g.chat_id
 WHERE tc.telegram_chat_id = ANY(@GroupIds) AND r.status = 'open' AND r.legacy_key LIKE 'redis:%'
   AND NOT EXISTS (SELECT 1 FROM recon_reports k
                    WHERE k.left_id = tc.telegram_chat_id AND k.right_id = r.report_message_id)";

            var orphans = removalsSkipped
                ? await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT count(*)::int " + scoped, new { GroupIds = groupIds }, transaction,
                    cancellationToken: cancellationToken))
                : await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE reports SET status = 'resolved', resolution_action = 'dismissed', resolved_at = now()
 WHERE report_id IN (SELECT r.report_id " + scoped + ")",
                    new { GroupIds = groupIds }, transaction, cancellationToken: cancellationToken));

            var missing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*)::int FROM recon_reports k
 WHERE NOT EXISTS (SELECT 1 FROM reports r
                     JOIN groups g ON g.group_id = r.group_id
                     JOIN telegram_chats tc ON tc.chat_id = g.chat_id
                    WHERE tc.telegram_chat_id = k.left_id AND r.report_message_id = k.right_id)",
                transaction: transaction, cancellationToken: cancellationToken));

            return new ReconciliationOutcome(orphans, missing, 0, removalsSkipped);
        }

        /// <summary>
        /// Audit events are durable history: differences are reported, never deleted. This replaces
        /// one query per event - 322,721 of them - with a single join.
        /// </summary>
        private static async Task<ReconciliationOutcome> ReconcileAuditAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, CancellationToken cancellationToken)
        {
            var missing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*)::int FROM recon_audit k
 WHERE k.source_key IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM group_audit_events e WHERE e.legacy_key = k.source_key)",
                transaction: transaction, cancellationToken: cancellationToken));
            return new ReconciliationOutcome(0, missing, 0, false);
        }

        /// <summary>
        /// Moderation counters were never compared at all - not even counted. They are absolute
        /// values, so a disagreement is a real divergence rather than a replay artefact.
        /// </summary>
        private static async Task<ReconciliationOutcome> ReconcileCountersAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, CancellationToken cancellationToken)
        {
            var missing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*)::int FROM recon_counters k
 WHERE NOT EXISTS (SELECT 1 FROM account_moderation_counters c
                     JOIN telegram_accounts ta ON ta.account_id = c.account_id
                    WHERE ta.telegram_user_id = k.id AND c.kind = k.kind)",
                transaction: transaction, cancellationToken: cancellationToken));

            var divergent = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*)::int FROM recon_counters k
  JOIN telegram_accounts ta ON ta.telegram_user_id = k.id
  JOIN account_moderation_counters c ON c.account_id = ta.account_id AND c.kind = k.kind
 WHERE c.count <> k.value",
                transaction: transaction, cancellationToken: cancellationToken));

            return new ReconciliationOutcome(0, missing, divergent, false);
        }

        /// <summary>
        /// Persists what was found. Mismatch counts previously survived only as a number in
        /// <c>migration_runs.details</c>, so "reconciliation ran clean" had no evidence behind it
        /// beyond a log line.
        /// </summary>
        private static async Task RecordMismatchesAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, MigrationRunTracker tracker, CancellationToken cancellationToken)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO migration_exceptions (migration_run_id, domain, source_key, severity, reason, created_at)
SELECT @RunId, 'operations', k.source_key, 'warning',
       'Present in Redis but missing from PostgreSQL after backfill.', now()
  FROM recon_sanctions k
 WHERE k.source_key IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sanctions s WHERE s.legacy_key = k.source_key)
UNION ALL
SELECT @RunId, 'operations', k.source_key, 'warning',
       'Audit event present in Redis but missing from PostgreSQL after backfill.', now()
  FROM recon_audit k
 WHERE k.source_key IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM group_audit_events e WHERE e.legacy_key = k.source_key)",
                new { tracker.RunId }, transaction, cancellationToken: cancellationToken));
        }

    }
}
