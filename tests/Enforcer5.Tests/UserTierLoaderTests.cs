using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.Loading;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The Stage B loader, end to end against real Redis and PostgreSQL.
/// <para>
/// The invariant it exists to preserve is
/// <c>message_count = SUM(per-group totals) + unattributed_count</c>. Recomputing the user total
/// from group sums instead of carrying it would lose 27,559,870 messages - 2.36% of all volume -
/// and zero 34,736 users outright, every one of them with a non-zero count.
/// </para>
/// </summary>
public sealed class UserTierLoaderTests
{
    [RedisPostgresFact]
    public async Task TheCarriedTotalAndItsGroupBreakdownSatisfyTheResidualInvariant()
    {
        var scope = Guid.NewGuid().ToString("N")[..8];
        var groupA = -100_000_000_000L - Rand();
        var groupB = -200_000_000_000L - Rand();
        var user = 300_000_000L + Rand();

        var seed = new Dictionary<string, (string Field, string Value)[]>
        {
            // Global lifetime total is 100, but the surviving groups only account for 30+50 = 80.
            // The missing 20 is real history whose group keys are gone, and it must survive as the
            // unattributed residual rather than being silently dropped.
            [$"user:{user}"] = [("name", $"Name{scope}"), ("username", $"user{scope}")],
            [$"chat:{user}"] = [("msgs", "100")],
            [$"{groupA}:users:{user}"] = [("msgs", "30")],
            [$"{groupB}:users:{user}"] = [("msgs", "50")]
        };

        await InEnvironmentAsync(seed, async (database, redis) =>
        {
            var generation = await new KeyspaceInventoryScanner(database, redis).ScanAsync();
            var tracker = await MigrationRunTracker.StartAsync(database, "user-tier-load-test");
            var result = await new UserTierLoader(database, redis).LoadAsync(generation.GenerationId, tracker);

            Assert.True(result.UsersStaged >= 1);
            await using var connection = await database.OpenConnectionAsync();

            var totals = await connection.QuerySingleAsync<(long MessageCount, long Unattributed)>(@"
SELECT t.message_count AS MessageCount, t.unattributed_count AS Unattributed
  FROM account_message_totals t
  JOIN telegram_accounts a ON a.account_id = t.account_id
 WHERE a.telegram_user_id = @User", new { User = user });
            Assert.Equal(100, totals.MessageCount);
            Assert.Equal(20, totals.Unattributed);

            // The prototype loaded the group counters only to sum them for the residual and never
            // wrote them, so every per-group total would have arrived at zero.
            var perGroup = (await connection.QueryAsync<(long ChatId, long Messages)>(@"
SELECT c.telegram_chat_id AS ChatId, t.message_count AS Messages
  FROM group_account_message_totals t
  JOIN telegram_accounts a ON a.account_id = t.account_id
  JOIN groups g ON g.group_id = t.group_id
  JOIN telegram_chats c ON c.chat_id = g.chat_id
 WHERE a.telegram_user_id = @User ORDER BY c.telegram_chat_id", new { User = user })).ToList();
            Assert.Equal(2, perGroup.Count);
            Assert.Equal(80, perGroup.Sum(x => x.Messages));

            // The invariant, stated directly.
            Assert.Equal(totals.MessageCount, perGroup.Sum(x => x.Messages) + totals.Unattributed);
        });
    }

    [RedisPostgresFact]
    public async Task AUserThatExistsOnlyAsACounterIsStillMigrated()
    {
        // 34,736 users have a chat:{userId} total but no user:{id} record, all with non-zero
        // counts. Seeding the population from user:* alone drops every one of them.
        var user = 400_000_000L + Rand();
        var seed = new Dictionary<string, (string Field, string Value)[]>
        {
            [$"chat:{user}"] = [("msgs", "77")]
        };

        await InEnvironmentAsync(seed, async (database, redis) =>
        {
            var generation = await new KeyspaceInventoryScanner(database, redis).ScanAsync();
            var tracker = await MigrationRunTracker.StartAsync(database, "user-tier-load-test");
            await new UserTierLoader(database, redis).LoadAsync(generation.GenerationId, tracker);

            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(77, await connection.ExecuteScalarAsync<long>(@"
SELECT t.message_count FROM account_message_totals t
  JOIN telegram_accounts a ON a.account_id = t.account_id
 WHERE a.telegram_user_id = @User", new { User = user }));
        });
    }

    [RedisPostgresFact]
    public async Task AUserWithNoNameIsMigratedAndTheDefectRecorded()
    {
        // Discarding these is the defect that lost 326,787 users - 6.38% of the estate. Telegram
        // always supplies first_name, so absence is a defective write, not grounds for deletion.
        var user = 500_000_000L + Rand();
        var seed = new Dictionary<string, (string Field, string Value)[]>
        {
            [$"user:{user}"] = [("msgs", "3")],
            [$"chat:{user}"] = [("msgs", "3")]
        };

        await InEnvironmentAsync(seed, async (database, redis) =>
        {
            var generation = await new KeyspaceInventoryScanner(database, redis).ScanAsync();
            var tracker = await MigrationRunTracker.StartAsync(database, "user-tier-load-test");
            var result = await new UserTierLoader(database, redis).LoadAsync(generation.GenerationId, tracker);

            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM telegram_accounts WHERE telegram_user_id = @User",
                new { User = user }));
            Assert.True(result.MissingNames >= 1);
            // Recorded as a warning, not blocking: it is the point of the run, not a failure.
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>(@"
SELECT count(*) FROM migration_exceptions
 WHERE severity = 'warning' AND source_field = 'name' AND source_key = @Key",
                new { Key = $"user:{user}" }));
        });
    }

    [RedisPostgresFact]
    public async Task ASecondLoadConvergesInsteadOfDuplicating()
    {
        var user = 600_000_000L + Rand();
        var group = -300_000_000_000L - Rand();
        var seed = new Dictionary<string, (string Field, string Value)[]>
        {
            [$"user:{user}"] = [("name", "Repeat")],
            [$"chat:{user}"] = [("msgs", "9")],
            [$"{group}:users:{user}"] = [("msgs", "9")]
        };

        await InEnvironmentAsync(seed, async (database, redis) =>
        {
            var scanner = new KeyspaceInventoryScanner(database, redis);
            var loader = new UserTierLoader(database, redis);

            var first = await scanner.ScanAsync();
            await loader.LoadAsync(first.GenerationId,
                await MigrationRunTracker.StartAsync(database, "user-tier-load-test"));
            var second = await scanner.ScanAsync(resume: false);
            await loader.LoadAsync(second.GenerationId,
                await MigrationRunTracker.StartAsync(database, "user-tier-load-test"));

            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM telegram_accounts WHERE telegram_user_id = @User",
                new { User = user }));
            // One open profile per account: the partial unique index would reject a second, so a
            // re-run that did not guard this would fail rather than converge.
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>(@"
SELECT count(*) FROM telegram_account_profiles p
  JOIN telegram_accounts a ON a.account_id = p.account_id
 WHERE a.telegram_user_id = @User AND p.valid_to IS NULL", new { User = user }));
            Assert.Equal(9, await connection.ExecuteScalarAsync<long>(@"
SELECT t.message_count FROM account_message_totals t
  JOIN telegram_accounts a ON a.account_id = t.account_id
 WHERE a.telegram_user_id = @User", new { User = user }));
        });
    }

    private static long Rand() => Random.Shared.NextInt64(1, 100_000_000);

    private static async Task InEnvironmentAsync(
        Dictionary<string, (string Field, string Value)[]> seed,
        Func<PostgresDatabase, IDatabase, Task> body)
    {
        var redisConnection = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_usertier_{Guid.NewGuid():N}";

        using var config = GlobalConfigScope.Acquire("RedisConnection", "StorageMode");
        config.Set("RedisConnection", redisConnection);
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        Assert.True(Redis.Start());

        foreach (var (key, fields) in seed)
            foreach (var (field, value) in fields)
                await Redis.db.HashSetAsync(key, field, value);

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
            await Redis.db.KeyDeleteAsync(seed.Keys.Select(x => (RedisKey)x).ToArray());
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }
}
