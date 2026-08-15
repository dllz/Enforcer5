using Dapper;
using Enforcer5.Storage;
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
            var values = await repository.GetAsync(stale.ChatId, stale.SettingGroup);
            var revision = await connection.ExecuteScalarAsync<long>(@"
SELECT split_part(target_reference, ';revision:', 2)::bigint
FROM legacy_redis_records WHERE source_key = @RedisKey AND source_field = ''",
                new { stale.RedisKey });

            Assert.Equal("no", values!["Flood"]);
            Assert.Equal(9, revision);

            var deleted = stale with
            {
                SourceExists = false,
                SourceRevision = 11,
                Fields = new Dictionary<string, string>()
            };
            Assert.True(await repository.UpsertAsync(deleted));
            Assert.Empty(await repository.GetAsync(deleted.ChatId, deleted.SettingGroup));
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
