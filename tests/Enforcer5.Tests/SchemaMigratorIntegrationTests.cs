using Dapper;
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
            var ledgerCount = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM \"__EFMigrationsHistory\"");

            Assert.Equal(1, ledgerCount);
            Assert.Contains("migration_runs", tables);
            Assert.Contains("migration_exceptions", tables);
            Assert.Contains("legacy_redis_records", tables);
            Assert.Contains("compatibility_outbox", tables);
            Assert.Contains("group_settings", tables);
            Assert.Contains("group_settings_redis_sync", tables);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }
}
