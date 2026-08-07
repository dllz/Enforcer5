using Dapper;
using Enforcer5.Storage;
using Newtonsoft.Json;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

public sealed class GroupSettingsRepositoryIntegrationTests
{
    [PostgresFact]
    public async Task RepositoryRejectsStaleSnapshotsAndSupportsAuthoritativeRepair()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_settings_test_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await using var database = new PostgresDatabase(builder.ConnectionString);
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            var repository = new GroupSettingsRepository(database);
            var now = DateTimeOffset.UtcNow;

            var newer = Snapshot(10, new Dictionary<string, string>
            {
                ["Flood"] = "yes",
                ["Flagged"] = "no",
                ["unknown"] = "MixedCase"
            }, now);
            var stale = Snapshot(9, new Dictionary<string, string> { ["Flood"] = "no" }, now.AddSeconds(1));

            Assert.True(await repository.UpsertAsync(newer));
            Assert.False(await repository.UpsertAsync(stale));
            Assert.False(await repository.UpsertAsync(stale, force: true, expectedCurrentRevision: 8));
            Assert.True(await repository.UpsertAsync(stale, force: true, expectedCurrentRevision: 10));

            await using var connection = await database.OpenConnectionAsync();
            var json = await connection.ExecuteScalarAsync<string>(@"
SELECT values::text FROM group_settings
WHERE chat_id = @ChatId AND setting_group = @SettingGroup",
                new { stale.ChatId, stale.SettingGroup });
            var values = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            var revision = await connection.ExecuteScalarAsync<long>(@"
SELECT source_revision FROM group_settings_redis_sync
WHERE chat_id = @ChatId AND setting_group = @SettingGroup",
                new { stale.ChatId, stale.SettingGroup });

            Assert.Equal("no", values!["Flood"]);
            Assert.Equal(9, revision);

            var deleted = stale with
            {
                SourceExists = false,
                SourceRevision = 11,
                Fields = new Dictionary<string, string>()
            };
            Assert.True(await repository.UpsertAsync(deleted));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(@"
SELECT count(*) FROM group_settings
WHERE chat_id = @ChatId AND setting_group = @SettingGroup",
                new { deleted.ChatId, deleted.SettingGroup }));
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }

    private static GroupSettingsSnapshot Snapshot(long revision, IReadOnlyDictionary<string, string> fields,
        DateTimeOffset observedAt)
    {
        return new GroupSettingsSnapshot(-100123, GroupSettingGroups.Settings,
            "chat:-100123:settings", fields, true, revision, observedAt);
    }
}
