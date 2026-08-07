using Dapper;
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

public sealed class OperationsReportRepositoryIntegrationTests
{
    [PostgresFact]
    public async Task CreateIsIdempotentAndSolveUsesCompareAndSet()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_report_runtime_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            var repository = new ReportRepository(database);
            var created = DateTimeOffset.UtcNow;
            var report = new Report(0, -10042, 81, 7, "reporter", 8, 80,
                "open", created, null, null, null, null);

            var ids = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repository.UpsertAsync(report)));
            Assert.Single(ids.Distinct());
            Assert.True(await repository.TrySolveAsync(-10042, 81, 9, "solver", created.AddMinutes(1)));
            Assert.False(await repository.TrySolveAsync(-10042, 81, 10, "other", created.AddMinutes(2)));

            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM reports WHERE group_id = -10042 AND report_message_id = 81"));
            var solvedBy = await connection.ExecuteScalarAsync<long>(
                "SELECT solved_by_user_id FROM reports WHERE group_id = -10042 AND report_message_id = 81");
            Assert.Equal(9, solvedBy);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }
}
