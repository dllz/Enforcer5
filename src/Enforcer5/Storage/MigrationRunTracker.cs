using System;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;

namespace Enforcer5.Storage
{
    /// <summary>
    /// Severities accepted by <c>migration_exceptions</c>.
    /// </summary>
    internal static class MigrationSeverities
    {
        /// <summary>Observed and worth knowing, but not a defect.</summary>
        internal const string Info = "info";
        /// <summary>A data-quality defect that does not stop the migration - the point of the run.</summary>
        internal const string Warning = "warning";
        /// <summary>Stops the run from being considered successful.</summary>
        internal const string Blocking = "blocking";
    }

    internal sealed class MigrationRunTracker
    {
        private readonly PostgresDatabase _database;

        private MigrationRunTracker(PostgresDatabase database, long runId)
        {
            _database = database;
            RunId = runId;
        }

        /// <summary>
        /// The <c>migration_runs</c> row this tracker owns. Exposed so a set-based writer can
        /// attach thousands of exception rows in one statement instead of one round trip each.
        /// </summary>
        internal long RunId { get; }

        internal static async Task<MigrationRunTracker> StartAsync(PostgresDatabase database, string mode)
        {
            await using var connection = await database.OpenConnectionAsync();
            var id = await connection.ExecuteScalarAsync<long>(@"
INSERT INTO migration_runs (mode, status) VALUES (@Mode, 'running') RETURNING id",
                new { Mode = mode });
            return new MigrationRunTracker(database, id);
        }

        /// <summary>
        /// Records one exception against this run.
        /// <para>
        /// Severity is a parameter rather than always <c>blocking</c>. A reconciliation mismatch and
        /// a malformed Redis value are not the same thing, and a run that reports every observation
        /// as blocking gives no way to tell a clean run from a noisy one.
        /// </para>
        /// </summary>
        internal async Task AddExceptionAsync(string domain, string reason, string rawValue = null,
            string severity = MigrationSeverities.Blocking, string sourceKey = null,
            string sourceField = null)
        {
            await using var connection = await _database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO migration_exceptions
    (migration_run_id, domain, source_key, source_field, severity, reason, raw_value)
VALUES (@RunId, @Domain, @SourceKey, @SourceField, @Severity, @Reason, @RawValue)",
                new
                {
                    RunId, Domain = domain, SourceKey = sourceKey, SourceField = sourceField,
                    Severity = severity, Reason = reason, RawValue = rawValue
                });
        }

        internal async Task FinishAsync(bool succeeded, object details)
        {
            await using var connection = await _database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE migration_runs SET status = @Status, completed_at = now(), details = CAST(@Details AS jsonb)
WHERE id = @RunId", new
            {
                RunId,
                Status = succeeded ? "completed" : "failed",
                Details = JsonConvert.SerializeObject(details ?? new { })
            });
        }

        internal async Task FailAsync(Exception exception)
        {
            await AddExceptionAsync("migration", exception.Message, exception.ToString());
            await FinishAsync(false, new { error = exception.Message });
        }
    }
}
