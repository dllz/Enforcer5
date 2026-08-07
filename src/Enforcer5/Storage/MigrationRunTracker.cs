using System;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;

namespace Enforcer5.Storage
{
    internal sealed class MigrationRunTracker
    {
        private readonly PostgresDatabase _database;
        private readonly long _runId;

        private MigrationRunTracker(PostgresDatabase database, long runId)
        {
            _database = database;
            _runId = runId;
        }

        internal static async Task<MigrationRunTracker> StartAsync(PostgresDatabase database, string mode)
        {
            await using var connection = await database.OpenConnectionAsync();
            var id = await connection.ExecuteScalarAsync<long>(@"
INSERT INTO migration_runs (mode, status) VALUES (@Mode, 'running') RETURNING id",
                new { Mode = mode });
            return new MigrationRunTracker(database, id);
        }

        internal async Task AddExceptionAsync(string domain, string reason, string rawValue = null)
        {
            await using var connection = await _database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO migration_exceptions
    (migration_run_id, domain, severity, reason, raw_value)
VALUES (@RunId, @Domain, 'blocking', @Reason, @RawValue)",
                new { RunId = _runId, Domain = domain, Reason = reason, RawValue = rawValue });
        }

        internal async Task FinishAsync(bool succeeded, object details)
        {
            await using var connection = await _database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE migration_runs SET status = @Status, completed_at = now(), details = CAST(@Details AS jsonb)
WHERE id = @RunId", new
            {
                RunId = _runId,
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
