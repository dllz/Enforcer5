using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.Loading;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The inventory is what removes the O(keyspace)-per-group problem, and Stage C's freshness rule
/// trusts it to decide whether a group may cut over. Both properties depend on it being complete
/// and on a generation only claiming completion once it really finished.
/// </summary>
public sealed class KeyspaceInventoryScannerTests
{
    [RedisPostgresFact]
    public async Task AFullPassRecordsEveryKeyClassifiedAndMarksTheGenerationComplete()
    {
        var prefix = $"invtest{Guid.NewGuid():N}";
        var groupId = -1000000000000L - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000000;
        var userId = 700000000L + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000000;

        var seeded = new Dictionary<string, string>
        {
            [$"user:{userId}"] = MigrationKeyClasses.User,
            [$"chat:{userId}"] = MigrationKeyClasses.UserMessageTotal,
            [$"{groupId}:users:{userId}"] = MigrationKeyClasses.GroupStatistics,
            [$"chat:{groupId}:settings"] = MigrationKeyClasses.GroupScoped,
            [$"chat:{groupId}links"] = MigrationKeyClasses.GroupScoped,
            [$"flagged:{groupId}:4242"] = MigrationKeyClasses.Report,
            [$"globalBan:{userId}"] = MigrationKeyClasses.GlobalBan,
            [$"ban:{userId}"] = MigrationKeyClasses.ModerationCounters,
            [$"{prefix}:completely:unknown"] = MigrationKeyClasses.Unclassified
        };

        await InRedisAndSchemaAsync(seeded.Keys, async (database, redis) =>
        {
            var result = await new KeyspaceInventoryScanner(database, redis).ScanAsync();

            Assert.True(result.Completed);
            await using var connection = await database.OpenConnectionAsync();

            foreach (var (key, expectedClass) in seeded)
            {
                var recorded = await connection.QuerySingleOrDefaultAsync<string>(
                    "SELECT key_class FROM migration_key_inventory " +
                    "WHERE generation_id = @Generation AND redis_key = @Key",
                    new { Generation = result.GenerationId, Key = key });
                Assert.Equal(expectedClass, recorded);
            }

            // The per-group lookup that replaces a full SCAN MATCH per group. The group's own keys
            // include the colon-less "links" shape and the prefixless statistics shape, which a
            // naive prefix match would miss.
            var groupKeys = (await connection.QueryAsync<string>(
                "SELECT redis_key FROM migration_key_inventory " +
                "WHERE generation_id = @Generation AND telegram_chat_id = @ChatId",
                new { Generation = result.GenerationId, ChatId = groupId })).ToHashSet();
            Assert.Contains($"chat:{groupId}links", groupKeys);
            Assert.Contains($"{groupId}:users:{userId}", groupKeys);
            Assert.Contains($"flagged:{groupId}:4242", groupKeys);

            // An unrecognised shape is counted so it can be investigated, never silently dropped.
            Assert.True(result.UnclassifiedCount >= 1);

            var generation = await connection.QuerySingleAsync<(string Status, DateTimeOffset? CompletedAt, string Cursor)>(
                "SELECT status AS Status, completed_at AS CompletedAt, cursor AS Cursor " +
                "FROM migration_scan_generations WHERE generation_id = @Generation",
                new { Generation = result.GenerationId });
            Assert.Equal("completed", generation.Status);
            Assert.NotNull(generation.CompletedAt);
            // The cursor is cleared on completion: a finished generation has no resume point.
            Assert.Null(generation.Cursor);
        });
    }

    [RedisPostgresFact]
    public async Task RescanningIsIdempotentBecauseScanCanReturnTheSameKeyTwice()
    {
        var userId = 800000000L + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000000;
        var keys = new[] { $"user:{userId}", $"chat:{userId}" };

        await InRedisAndSchemaAsync(keys, async (database, redis) =>
        {
            var scanner = new KeyspaceInventoryScanner(database, redis);
            var first = await scanner.ScanAsync();
            // A second pass with resume disabled starts a new generation, so the two must not
            // collide - the inventory is keyed per generation precisely so a re-scan is safe.
            var second = await scanner.ScanAsync(resume: false);

            Assert.NotEqual(first.GenerationId, second.GenerationId);
            await using var connection = await database.OpenConnectionAsync();
            foreach (var generationId in new[] { first.GenerationId, second.GenerationId })
                Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
                    "SELECT count(*) FROM migration_key_inventory " +
                    "WHERE generation_id = @Generation AND redis_key = @Key",
                    new { Generation = generationId, Key = $"user:{userId}" }));

            var latest = await KeyspaceInventoryScanner.LatestCompletedAsync(database);
            Assert.NotNull(latest);
            Assert.Equal(second.GenerationId, latest!.Value.GenerationId);
        });
    }

    [RedisPostgresFact]
    public async Task AnUnfinishedGenerationIsNotOfferedAsTheFreshnessBaseline()
    {
        // Stage C reads LatestCompletedAsync to decide whether a group may cut over. A scan that
        // is still running covers only part of the keyspace, so offering it would authorise a
        // cutover against an inventory that never saw the group's keys.
        await InRedisAndSchemaAsync([], async (database, _) =>
        {
            await using (var connection = await database.OpenConnectionAsync())
                await connection.ExecuteAsync(
                    "INSERT INTO migration_scan_generations (status, cursor) VALUES ('running', '512')");

            Assert.Null(await KeyspaceInventoryScanner.LatestCompletedAsync(database));
        });
    }

    private static async Task InRedisAndSchemaAsync(IEnumerable<string> keys,
        Func<PostgresDatabase, IDatabase, Task> body)
    {
        var redisConnection = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_inventory_{Guid.NewGuid():N}";

        using var config = GlobalConfigScope.Acquire("RedisConnection", "StorageMode");
        config.Set("RedisConnection", redisConnection);
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        Assert.True(Redis.Start());

        var seeded = keys.ToArray();
        foreach (var key in seeded) await Redis.db.HashSetAsync(key, "msgs", 1);

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            await body(database, Redis.db);
        }
        finally
        {
            if (seeded.Length > 0)
                await Redis.db.KeyDeleteAsync(seeded.Select(x => (RedisKey)x).ToArray());
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }
}
