using Dapper;
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

public sealed class OperationsRuntimeIntegrationTests
{
    [PostgresFact]
    public async Task MessageStatisticsIncrementAtomicallyUnderConcurrency()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_operations_runtime_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            var repository = new StatisticsRepository(database);
            var occurredAt = DateTimeOffset.UtcNow;

            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
                repository.IncrementMessageAsync(Editions.Normal, 42, -10042, occurredAt)));

            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(20, await connection.ExecuteScalarAsync<long>(
                "SELECT messages FROM user_statistics WHERE user_id = 42"));
            Assert.Equal(20, await connection.ExecuteScalarAsync<long>(
                "SELECT messages FROM group_user_statistics WHERE group_id = -10042 AND user_id = 42"));
            Assert.Equal(20, await connection.ExecuteScalarAsync<long>(
                "SELECT messages FROM bot_statistic_baselines WHERE edition = 'normal'"));
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }

    [PostgresFact]
    public async Task TempBanWorkerCompletesQueuedClaimsAndReleasesFailures()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_tempban_worker_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            var sanctions = new SanctionRepository(database);
            var now = DateTimeOffset.UtcNow;
            await sanctions.UpsertAsync(TempBan(1, now.AddMinutes(-2)));
            await sanctions.UpsertAsync(TempBan(2, now.AddMinutes(-1)));
            var queued = new List<long>();
            var worker = new PostgresTempBanDueWorker(sanctions, Editions.Normal, "test-worker",
                (groupId, userId, _) =>
                {
                    if (userId == 2) throw new InvalidOperationException("queue unavailable");
                    queued.Add(userId);
                    return Task.CompletedTask;
                });

            var result = await worker.ProcessDueAsync(10, now);

            Assert.Equal(new[] { 1L }, queued);
            Assert.Equal(new TempBanDueWorkerResult(2, 1, 1), result);
            await using var connection = await database.OpenConnectionAsync();
            var statuses = (await connection.QueryAsync<(long UserId, string Status)>(
                "SELECT user_id AS UserId, status AS Status FROM sanctions ORDER BY user_id")).ToArray();
            Assert.Equal((1L, SanctionStatuses.Completed), statuses[0]);
            Assert.Equal((2L, SanctionStatuses.Active), statuses[1]);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }

    private static Sanction TempBan(long userId, DateTimeOffset expiresAt) =>
        new(0, -10042, userId, SanctionKinds.TempBan, SanctionStatuses.Active,
            Editions.Normal, null, null, expiresAt.AddHours(-1), expiresAt,
            null, null, null, $"test:tempban:{userId}");
}
