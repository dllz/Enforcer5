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
                "SELECT t.message_count FROM account_message_totals t JOIN telegram_accounts a " +
                "ON a.account_id = t.account_id WHERE a.telegram_user_id = 42"));
            Assert.Equal(20, await connection.ExecuteScalarAsync<long>(
                "SELECT t.message_count FROM group_account_message_totals t " +
                "JOIN telegram_accounts a ON a.account_id = t.account_id " +
                "JOIN groups g ON g.group_id = t.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id " +
                "WHERE c.telegram_chat_id = -10042 AND a.telegram_user_id = 42"));
            Assert.Equal(20, await connection.ExecuteScalarAsync<long>(
                "SELECT message_count FROM bot_statistic_baselines WHERE edition = 'normal'"));
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

        // SanctionRepository.CompleteClaimAsync branches on StorageModeProvider.RedisMirrorEnabled.
        // Under a leaked mirror mode it takes the outbox path and the claim is released instead of
        // completed, so pin the mode for the duration of this test.
        using var config = GlobalConfigScope.Acquire("StorageMode", "PostgresConnection");
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));

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

            // The PostgreSQL expiry worker only claims groups PostgreSQL owns, so that during a
            // mixed population it and the Redis worker partition the estate instead of both acting
            // on a group - or neither.
            await using (var stateConnection = new NpgsqlConnection(builder.ConnectionString))
            {
                await stateConnection.OpenAsync();
                await stateConnection.ExecuteAsync(
                    "INSERT INTO migration_group_state (telegram_chat_id, state) VALUES (-10042, 'Postgres')");
            }

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
                "SELECT a.telegram_user_id AS UserId, s.status AS Status FROM sanctions s " +
                "JOIN telegram_accounts a ON a.account_id = s.account_id ORDER BY a.telegram_user_id")).ToArray();
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
