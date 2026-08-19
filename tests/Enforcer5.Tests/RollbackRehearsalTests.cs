using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.Cutover;
using Enforcer5.Storage.Moderation;
using Enforcer5.Storage.Operations;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The rollback rehearsal: flip a group to PostgreSQL, write to it, flip it back, and check Redis
/// holds everything that happened while it was migrated.
/// <para>
/// This is the gate the rollout has never had. Rollback from the per-group phase is supposed to be
/// a state flip rather than a restore, which is only true if every group-scoped write is mirrored
/// back into Redis. Four paths were not - sanction revocation, report policies, per-group message
/// totals, and group content and custom commands - and the reason nothing caught it is exactly
/// that this rehearsal did not exist.
/// </para>
/// <para>
/// The condition matters: the process-wide mode stays <c>RedisPrimaryPostgresShadow</c> throughout,
/// because that is what Stage C looks like. Only the group's own state says PostgreSQL. A test that
/// flipped the process-wide mode instead would pass while the bug was present.
/// </para>
/// </summary>
[Collection("GroupDrainBuffer")]
public sealed class RollbackRehearsalTests
{
    [RedisPostgresFact]
    public async Task WritesMadeWhileAGroupIsMigratedSurviveARollbackToRedis()
    {
        var chatId = -1000000000000L - Random.Shared.NextInt64(1, 100_000_000);
        var userId = 900_000_000L + Random.Shared.NextInt64(1, 100_000_000);

        await InEnvironmentAsync(chatId, async (database, config) =>
        {
            // --- the group cuts over -------------------------------------------------------
            GroupMigrationStateStore.ApplyLocal(chatId, GroupMigrationState.Postgres);
            Assert.True(StorageRouting.GroupPostgresIsPrimary(chatId));
            Assert.True(StorageRouting.GroupRedisMirrorEnabled(chatId));
            // The process-wide mirror is off, which is the whole point: these writes are only
            // mirrored if they consult the group's state rather than the process-wide mode.
            Assert.False(StorageRouting.RedisMirrorEnabled);

            // --- writes while it is on PostgreSQL ------------------------------------------
            await GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Settings, "welcome", "hello");
            await WarningStore.IssueAsync(chatId, userId, WarningTypes.Standard);
            await RoleStore.ElevateAsync(chatId, userId);
            await PolicyStore.SetWatchDisabledAsync(chatId, userId, true);

            // The paths that were gated on the process-wide flag.
            await ReportPolicyStore.SetReporterBlockedAsync(chatId, userId, true);
            await OperationsStore.RecordSanctionAsync(chatId, userId, SanctionKinds.Ban);
            await OperationsStore.RecordSanctionResolvedAsync(chatId, userId, SanctionKinds.Ban);
            await OperationsStore.RecordMessageAsync(userId, chatId, DateTimeOffset.UtcNow);

            // --- flush the mirror, then roll back ------------------------------------------
            var applied = await new CompatibilityOutboxWorker(database).DrainAsync();
            Assert.True(applied > 0, "the mirror had nothing to apply, so nothing was ever queued");

            GroupMigrationStateStore.ApplyLocal(chatId, GroupMigrationState.Redis);
            Assert.False(StorageRouting.GroupPostgresIsPrimary(chatId));

            // --- Redis must hold everything ------------------------------------------------
            Assert.Equal("hello",
                (await Redis.db.HashGetAsync($"chat:{chatId}:settings", "welcome")).ToString());
            Assert.Equal("1", (await Redis.db.HashGetAsync($"chat:{chatId}:warns", userId)).ToString());
            Assert.True(await Redis.db.SetContainsAsync($"chat:{chatId}:mod", userId));
            Assert.True(await Redis.db.SetContainsAsync($"chat:{chatId}:watch", userId));

            // Report policy - was unmirrored.
            Assert.True(await Redis.db.SetContainsAsync($"chat:{chatId}:reportblocked", userId));

            // The ban was imposed and then lifted. Both halves have to have reached Redis: if only
            // the ban mirrored, the rollback would leave a user banned who had been unbanned.
            Assert.False(await Redis.db.SetContainsAsync($"chat:{chatId}:banned", userId));

            // Per-group message total - was unmirrored, along with the user total it moves with.
            Assert.Equal("1",
                (await Redis.db.HashGetAsync($"{chatId}:users:{userId}", "msgs")).ToString());
        });
    }

    [RedisPostgresFact]
    public async Task AnUnmirroredWriteIsWhatARollbackWouldLose()
    {
        // The negative control. Under PostgresOnly nothing is mirrored, so a write made while the
        // group is migrated is genuinely gone on rollback. This is what the four defects looked
        // like, and it is why PostgresOnly is the point of no return rather than another flip.
        var chatId = -1000000000000L - Random.Shared.NextInt64(1, 100_000_000);
        var userId = 910_000_000L + Random.Shared.NextInt64(1, 100_000_000);

        // The outer scope already owns the config gate, so the mode is changed through it rather
        // than by nesting an Acquire - GlobalConfigScope is a non-reentrant semaphore.
        await InEnvironmentAsync(chatId, async (database, config) =>
        {
            config.Set("StorageMode", nameof(StorageMode.PostgresOnly));
            Assert.False(StorageRouting.GroupRedisMirrorEnabled(chatId));

            await GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Settings, "welcome", "gone");
            Assert.Equal(0, await new CompatibilityOutboxWorker(database).DrainAsync());

            config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
            GroupMigrationStateStore.ApplyLocal(chatId, GroupMigrationState.Redis);

            Assert.False((await Redis.db.HashGetAsync($"chat:{chatId}:settings", "welcome")).HasValue);
        });
    }

    private static async Task InEnvironmentAsync(long chatId,
        Func<PostgresDatabase, GlobalConfigScope, Task> body)
    {
        var redisConnection = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_rollback_{Guid.NewGuid():N}";

        GroupMigrationStateStore.ResetForTests();
        using var config = GlobalConfigScope.Acquire("RedisConnection", "StorageMode",
            "PostgresConnection", "GroupSettingsShadowEnabled", "ModerationShadowEnabled",
            "OperationsShadowEnabled");
        config.Set("RedisConnection", redisConnection);
        // Stage C: the process-wide mode has not flipped. Only the group's state has.
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        config.Set("GroupSettingsShadowEnabled", "false");
        config.Set("ModerationShadowEnabled", "false");
        config.Set("OperationsShadowEnabled", "false");
        Assert.True(Redis.Start());

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        var keys = new List<RedisKey>
        {
            $"chat:{chatId}:settings", $"chat:{chatId}:warns", $"chat:{chatId}:totalWarns",
            $"chat:{chatId}:mod", $"chat:{chatId}:watch", $"chat:{chatId}:reportblocked",
            $"chat:{chatId}:banned", $"chat:{chatId}:userlast", $"chat:{chatId}:chatlast",
            $"compat:group-settings:{chatId}:settings:revision", GroupDrainBuffer.KeyFor(chatId)
        };
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            config.Set("PostgresConnection", builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            await Redis.db.KeyDeleteAsync(keys.ToArray());
            await body(database, config);
        }
        finally
        {
            await Redis.db.KeyDeleteAsync(keys.ToArray());
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
            GroupMigrationStateStore.ResetForTests();
        }
    }
}
