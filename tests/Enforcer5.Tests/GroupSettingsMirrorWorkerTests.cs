using Dapper;
using Enforcer5.Storage;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

public sealed class GroupSettingsMirrorWorkerTests
{
    /// <summary>
    /// The rollback-mirror worker was a one-shot static: the first call bound it to whatever
    /// PostgresConnection was set at the time and it stayed bound for the life of the process,
    /// so a later connection or storage-mode change left it writing to a stale database.
    /// </summary>
    [PostgresFact]
    public async Task MirrorWorkerRebindsWhenConnectionChangesAndStopsWhenModeLeavesMirror()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var firstSchema = $"enforcer_mirror_first_{Guid.NewGuid():N}";
        var secondSchema = $"enforcer_mirror_second_{Guid.NewGuid():N}";

        using var config = GlobalConfigScope.Acquire(
            "StorageMode", "PostgresConnection", "GroupSettingsShadowEnabled");
        // Keep the Redis shadow writers out of this test; only the mirror worker is under test.
        config.Set("GroupSettingsShadowEnabled", "false");

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{firstSchema}\"");
        await admin.ExecuteAsync($"CREATE SCHEMA \"{secondSchema}\"");
        try
        {
            var first = new NpgsqlConnectionStringBuilder(baseConnectionString)
                { SearchPath = firstSchema }.ConnectionString;
            var second = new NpgsqlConnectionStringBuilder(baseConnectionString)
                { SearchPath = secondSchema }.ConnectionString;
            await SchemaMigrationRunner.ApplyAsync(first);
            await SchemaMigrationRunner.ApplyAsync(second);

            config.Set("StorageMode", nameof(StorageMode.PostgresPrimaryRedisMirror));
            config.Set("PostgresConnection", first);
            await GroupSettingsStore.StartAsync();
            var built = GroupSettingsStore.MirrorWorkerGeneration;
            Assert.Equal(first, GroupSettingsStore.MirrorWorkerConnection);

            // Repointing the connection must build a new worker, not just record a new string
            // against the original one.
            config.Set("PostgresConnection", second);
            await GroupSettingsStore.StartAsync();
            Assert.Equal(second, GroupSettingsStore.MirrorWorkerConnection);
            Assert.Equal(built + 1, GroupSettingsStore.MirrorWorkerGeneration);

            // Starting again with unchanged configuration must be a no-op, not a rebuild.
            await GroupSettingsStore.StartAsync();
            Assert.Equal(second, GroupSettingsStore.MirrorWorkerConnection);
            Assert.Equal(built + 1, GroupSettingsStore.MirrorWorkerGeneration);

            // Leaving mirror mode must stop the worker rather than leave it draining an outbox.
            config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
            await GroupSettingsStore.StartAsync();
            Assert.Null(GroupSettingsStore.MirrorWorkerConnection);
        }
        finally
        {
            // Leave no worker running; a leaked background worker would contaminate later tests.
            config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
            await GroupSettingsStore.StartAsync();
            await admin.ExecuteAsync($"DROP SCHEMA \"{firstSchema}\" CASCADE");
            await admin.ExecuteAsync($"DROP SCHEMA \"{secondSchema}\" CASCADE");
        }
    }
}
