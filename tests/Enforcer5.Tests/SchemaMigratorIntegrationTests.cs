using Dapper;
using Microsoft.EntityFrameworkCore;
using Enforcer5.Storage;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

public sealed class SchemaMigratorIntegrationTests
{
    [PostgresFact]
    public async Task InitialSchemaIsTransactionalAndIdempotent()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_test_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);

            await using var connection = await database.OpenConnectionAsync();
            var tables = (await connection.QueryAsync<string>(@"
SELECT table_name
FROM information_schema.tables
WHERE table_schema = @Schema", new { Schema = schema })).ToHashSet();
            var applied = (await connection.QueryAsync<string>(
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"")).ToHashSet();

            // Assert against the migrations the assembly actually declares rather than a hardcoded
            // count, so adding a migration does not fail this test while a partial or repeated
            // apply - which is what the test is really about - still does.
            await using var context = EnforcerDbContextFactory.Create(builder.ConnectionString);
            var declared = context.Database.GetMigrations().ToHashSet();

            Assert.NotEmpty(declared);
            Assert.Equal(declared, applied);
            Assert.Contains("migration_runs", tables);
            Assert.Contains("migration_exceptions", tables);
            Assert.Contains("legacy_redis_records", tables);
            Assert.Contains("compatibility_outbox", tables);
            Assert.Contains("group_settings", tables);
            Assert.Contains("telegram_accounts", tables);
            Assert.Contains("telegram_chats", tables);
            Assert.Contains("group_user_preferences", tables);
            Assert.Contains("analytics_flush_batches", tables);
            Assert.Contains("custom_command_conflict_candidates", tables);
            Assert.DoesNotContain("group_settings_redis_sync", tables);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }
}
