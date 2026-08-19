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
            await OperationsStore.RecordMessageAsync(userId, chatId, DateTimeOffset.UtcNow);

            // A tempban imposed and then lifted. This exercises all three keys the mirror writes,
            // which is what makes the asymmetry detectable - the revoke used to remove only the set.
            await OperationsStore.RecordSanctionAsync(chatId, userId, SanctionKinds.TempBan, null,
                DateTimeOffset.UtcNow.AddDays(1));
            await OperationsStore.RecordSanctionResolvedAsync(chatId, userId, SanctionKinds.TempBan);

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

            // The tempban was imposed and then lifted, so every key the add wrote must be gone.
            // Asserting on the set alone would have passed while the expiry string and the global
            // hash row survived - and those two are precisely what a later re-read of Redis would
            // consult, so leaving them is what would resurrect a lifted ban.
            Assert.False(await Redis.db.SetContainsAsync($"chat:{chatId}:tempbanned", userId));
            Assert.False(await Redis.db.KeyExistsAsync($"chat:{chatId}:tempbanned:{userId}"));
            var globalRows = (await Redis.db.HashGetAllAsync("tempbanned"))
                .Count(x => x.Value.ToString().StartsWith($"{chatId}:{userId}:", StringComparison.Ordinal));
            Assert.Equal(0, globalRows);

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

    [RedisPostgresFact]
    public async Task MirroringATempBanDoesNotEvictAnotherGroupsEntryFromTheGlobalIndex()
    {
        // The global index is keyed by expiry second across every group, so two bans expiring in
        // the same second contend for one field. The runtime ban path walks forward to a free slot;
        // the mirror used a plain HSET and did not. The entry it overwrote could belong to a group
        // that has NOT migrated - whose entry is the one the Redis expiry worker still reads - so
        // that user would never be unbanned.
        var migrated = -1006600000000L - Random.Shared.NextInt64(1, 1_000_000);
        var bystander = -1006600000000L - Random.Shared.NextInt64(1_000_001, 2_000_000);
        var migratedUser = 877_000_000L + Random.Shared.NextInt64(1, 1_000_000);
        var bystanderUser = 888_000_000L + Random.Shared.NextInt64(1, 1_000_000);

        await InEnvironmentAsync(migrated, async (database, config) =>
        {
            GroupMigrationStateStore.ApplyLocal(migrated, GroupMigrationState.Postgres);
            var expiresAt = DateTimeOffset.UtcNow.AddDays(1);
            var second = expiresAt.ToUnixTimeSeconds();

            // A group still on Redis already occupies that second.
            await Redis.db.HashSetAsync("tempbanned", second, $"{bystander}:{bystanderUser}:Nick:Group");

            await OperationsStore.RecordSanctionAsync(migrated, migratedUser, SanctionKinds.TempBan,
                null, expiresAt);
            await new CompatibilityOutboxWorker(database).DrainAsync();

            try
            {
                // The bystander's entry must survive untouched - it is the one with a live reader.
                Assert.Equal($"{bystander}:{bystanderUser}:Nick:Group",
                    (await Redis.db.HashGetAsync("tempbanned", second)).ToString());

                // And the mirrored ban must still be findable, in the next free slot.
                var mirrored = (await Redis.db.HashGetAllAsync("tempbanned"))
                    .Where(x => x.Value.ToString().StartsWith($"{migrated}:{migratedUser}:",
                        StringComparison.Ordinal))
                    .ToArray();
                var slot = Assert.Single(mirrored);
                Assert.NotEqual(second.ToString(), slot.Name.ToString());

                // The per-user key stores the slot actually claimed, not the requested expiry -
                // RemoveTempBan reads it to find the row to delete, so a mismatch orphans it.
                Assert.Equal(slot.Name.ToString(),
                    (await Redis.db.StringGetAsync($"chat:{migrated}:tempbanned:{migratedUser}")).ToString());
            }
            finally
            {
                await Redis.db.HashDeleteAsync("tempbanned", second.ToString());
                foreach (var entry in await Redis.db.HashGetAllAsync("tempbanned"))
                    if (entry.Value.ToString().StartsWith($"{migrated}:", StringComparison.Ordinal))
                        await Redis.db.HashDeleteAsync("tempbanned", entry.Name);
                await Redis.db.KeyDeleteAsync($"chat:{migrated}:tempbanned:{migratedUser}");
            }
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
            $"chat:{chatId}:tempbanned",
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
            // The per-user expiry keys carry a generated id, so they are swept rather than listed.
            var cursor = "0";
            do
            {
                var page = (RedisResult[])await Redis.db.ExecuteAsync("SCAN", cursor, "MATCH",
                    $"chat:{chatId}:tempbanned:*", "COUNT", 1000);
                cursor = page[0].ToString();
                foreach (var key in (RedisResult[])page[1]) keys.Add(key.ToString());
            } while (cursor != "0");

            await Redis.db.KeyDeleteAsync(keys.ToArray());
            foreach (var hash in new[] { "tempbanned", "tempbannedPremium" })
                foreach (var entry in await Redis.db.HashGetAllAsync(hash))
                    if (entry.Value.ToString().StartsWith($"{chatId}:", StringComparison.Ordinal))
                        await Redis.db.HashDeleteAsync(hash, entry.Name);

            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
            GroupMigrationStateStore.ResetForTests();
        }
    }
}
