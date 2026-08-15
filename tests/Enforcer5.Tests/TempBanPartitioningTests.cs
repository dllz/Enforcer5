using Dapper;
using Enforcer5.Storage;
using Enforcer5.Storage.Cutover;
using Enforcer5.Storage.Operations;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The global tempban hash spans every group and has no group dimension, while only one expiry
/// worker used to run - chosen once at start-up from the process-wide storage mode. A group that
/// cut over therefore kept its entries in Redis with no worker acting on them, and PostgreSQL would
/// not act either, so its users stayed banned indefinitely. Both workers now filter on per-group
/// state so they partition the estate.
/// </summary>
public sealed class TempBanPartitioningTests : IDisposable
{
    // These read and write process-global StorageMode, so they must not interleave with each other
    // or with any other test observing it.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly GlobalConfigScope _config;

    public TempBanPartitioningTests()
    {
        Gate.Wait();
        GroupMigrationStateStore.ResetForTests();
        _config = GlobalConfigScope.Acquire("StorageMode");
        _config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
    }

    public void Dispose()
    {
        _config.Dispose();
        GroupMigrationStateStore.ResetForTests();
        Gate.Release();
    }

    [PostgresFact]
    public async Task PostgresWorkerIgnoresGroupsThatRedisStillOwns()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_tempban_partition_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            var sanctions = new SanctionRepository(database);
            var now = DateTimeOffset.UtcNow;

            const long redisOwned = -20001;
            const long postgresOwned = -20002;
            await sanctions.UpsertAsync(TempBan(redisOwned, 11, now.AddMinutes(-5)));
            await sanctions.UpsertAsync(TempBan(postgresOwned, 22, now.AddMinutes(-5)));

            await using (var connection = new NpgsqlConnection(builder.ConnectionString))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    "INSERT INTO migration_group_state (telegram_chat_id, state) VALUES (@P, 'Postgres')",
                    new { P = postgresOwned });
                // redisOwned is deliberately absent: no row at all must read as Redis-owned, since
                // that is the state of every group before the migration touches it.
            }

            var unbanned = new List<long>();
            var worker = new PostgresTempBanDueWorker(sanctions, Editions.Normal, "partition-test",
                (_, userId, _) => { unbanned.Add(userId); return Task.CompletedTask; });

            var result = await worker.ProcessDueAsync(10, now);

            Assert.Equal(new[] { 22L }, unbanned);
            Assert.Equal(1, result.Claimed);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    [PostgresFact]
    public async Task LeaseIsExclusiveAndReclaimableAfterExpiry()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_lease_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            const long chatId = -30001;

            var first = await GroupMigrationStateStore.TryAcquireLeaseAsync(
                database, chatId, "migrator-a", TimeSpan.FromMinutes(5));
            Assert.NotNull(first);

            // A second migrator must not be able to work the same group concurrently.
            var second = await GroupMigrationStateStore.TryAcquireLeaseAsync(
                database, chatId, "migrator-b", TimeSpan.FromMinutes(5));
            Assert.Null(second);

            // Only the lease holder may advance the group.
            var stolen = new GroupLease(chatId, "migrator-b", DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.False(await GroupMigrationStateStore.TransitionAsync(
                database, stolen, GroupMigrationState.Postgres));
            Assert.True(await GroupMigrationStateStore.TransitionAsync(
                database, first, GroupMigrationState.Draining));
            Assert.Equal(GroupMigrationState.Draining, GroupMigrationStateStore.Get(chatId));

            // A crashed migrator must not strand the group forever.
            var expired = await GroupMigrationStateStore.TryAcquireLeaseAsync(
                database, chatId, "migrator-a", TimeSpan.FromSeconds(-30));
            Assert.NotNull(expired);
            var reclaimed = await GroupMigrationStateStore.TryAcquireLeaseAsync(
                database, chatId, "migrator-b", TimeSpan.FromMinutes(5));
            Assert.NotNull(reclaimed);

            await GroupMigrationStateStore.ReleaseLeaseAsync(database, reclaimed);
            var afterRelease = await GroupMigrationStateStore.TryAcquireLeaseAsync(
                database, chatId, "migrator-c", TimeSpan.FromMinutes(5));
            Assert.NotNull(afterRelease);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    [PostgresFact]
    public async Task PrimeLoadsOnlyGroupsPastRedis()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_prime_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);

            await using (var connection = new NpgsqlConnection(builder.ConnectionString))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(@"
INSERT INTO migration_group_state (telegram_chat_id, state) VALUES
    (-40001, 'Redis'), (-40002, 'Postgres'), (-40003, 'Draining')");
            }

            await GroupMigrationStateStore.PrimeAsync(database);

            // Redis is the default, so those rows are not worth holding in the hot-path cache.
            Assert.Equal(GroupMigrationState.Redis, GroupMigrationStateStore.Get(-40001));
            Assert.Equal(GroupMigrationState.Postgres, GroupMigrationStateStore.Get(-40002));
            Assert.Equal(GroupMigrationState.Draining, GroupMigrationStateStore.Get(-40003));
            // An unknown group reads as Redis, which is the safe direction.
            Assert.Equal(GroupMigrationState.Redis, GroupMigrationStateStore.Get(-49999));

            Assert.True(GroupMigrationStateStore.PostgresIsAuthoritative(-40002));
            // Draining still serves from Redis - that is what makes a failed cutover free.
            Assert.False(GroupMigrationStateStore.PostgresIsAuthoritative(-40003));

            var counts = await GroupMigrationStateStore.CountByStateAsync(database);
            Assert.Equal(1, counts[GroupMigrationState.Redis]);
            Assert.Equal(1, counts[GroupMigrationState.Postgres]);
            Assert.Equal(1, counts[GroupMigrationState.Draining]);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    /// <summary>
    /// Completing a claim reads the sanction back to build the Redis mirror payload, and that query
    /// LEFT JOINs the actor's account. An unqualified FOR UPDATE made PostgreSQL reject it outright,
    /// so the completion threw, the claim was released instead, and the tempban never expired.
    /// </summary>
    [PostgresFact]
    public async Task CompletingAClaimWorksWhenTheGroupMirrorsBackToRedis()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_mirrorclaim_{Guid.NewGuid():N}";

        _config.Set("StorageMode", nameof(StorageMode.PostgresPrimaryRedisMirror));

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            var sanctions = new SanctionRepository(database);
            var now = DateTimeOffset.UtcNow;

            const long chatId = -50001;
            await sanctions.UpsertAsync(TempBan(chatId, 77, now.AddMinutes(-5)));

            var unbanned = new List<long>();
            var worker = new PostgresTempBanDueWorker(sanctions, Editions.Normal, "mirror-test",
                (_, userId, _) => { unbanned.Add(userId); return Task.CompletedTask; });

            var result = await worker.ProcessDueAsync(10, now);

            Assert.Equal(new[] { 77L }, unbanned);
            Assert.Equal(1, result.Claimed);
            Assert.Equal(1, result.Completed);
            Assert.Equal(0, result.Released);

            // The resolve payload must have been enqueued for rollback. The earlier upsert mirrors
            // its own 'set' row, so filter to the operation this test is about.
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM compatibility_outbox WHERE domain = 'sanctions' AND operation = 'resolve'"));
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    private static Sanction TempBan(long chatId, long userId, DateTimeOffset expiresAt) =>
        new(0, chatId, userId, SanctionKinds.TempBan, SanctionStatuses.Active,
            Editions.Normal, null, null, expiresAt.AddHours(-1), expiresAt,
            null, null, null, $"test:partition:{chatId}:{userId}");
}
