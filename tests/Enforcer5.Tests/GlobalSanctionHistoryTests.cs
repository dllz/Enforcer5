using Dapper;
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// A revoked global ban is durable moderation history, not an absence.
/// <para>
/// The revoke path used to be an UPDATE only, so a record with no pre-existing active row produced
/// nothing at all - no row, no exception, no trace. That was exactly the 83 of 1,843
/// <c>globalBan</c> keys that never arrived in the rehearsal load, and it is the kind of silence the
/// rollout's first property forbids: every durable record either reaches PostgreSQL or is
/// deliberately and visibly excluded.
/// </para>
/// </summary>
public sealed class GlobalSanctionHistoryTests
{
    [PostgresFact]
    public async Task ARevokedBanWithNoPriorRowIsStillRecorded()
    {
        await InSchemaAsync(async database =>
        {
            var imposed = new DateTimeOffset(2020, 1, 1, 11, 22, 0, TimeSpan.Zero);
            var revoked = new DateTimeOffset(2020, 1, 1, 11, 30, 37, TimeSpan.Zero);

            await new GlobalSanctionRepository(database).UpsertAsync(new GlobalSanction(
                4242, false, "ads", imposed, "was a mistake", revoked, "redis:globalBan:4242"));

            await using var connection = await database.OpenConnectionAsync();
            var row = await connection.QuerySingleAsync<(string Status, string Reason,
                string ResolutionReason, DateTimeOffset ImposedAt, DateTimeOffset? ResolvedAt)>(@"
SELECT g.status AS Status, g.reason AS Reason, g.resolution_reason AS ResolutionReason,
       g.imposed_at AS ImposedAt, g.resolved_at AS ResolvedAt
  FROM global_sanctions g JOIN telegram_accounts a ON a.account_id = g.account_id
 WHERE a.telegram_user_id = 4242");

            Assert.Equal("revoked", row.Status);
            // Both halves of the history survive. One column could not hold both, which is why
            // resolution_reason was added rather than overwriting reason.
            Assert.Equal("ads", row.Reason);
            Assert.Equal("was a mistake", row.ResolutionReason);
            Assert.Equal(imposed, row.ImposedAt);
            Assert.Equal(revoked, row.ResolvedAt);
        });
    }

    [PostgresFact]
    public async Task RevokingAnActiveBanKeepsWhyTheUserWasBanned()
    {
        await InSchemaAsync(async database =>
        {
            var repository = new GlobalSanctionRepository(database);
            await repository.UpsertAsync(new GlobalSanction(4243, true, "spam",
                DateTimeOffset.UtcNow.AddDays(-2), null, null, "redis:globalBan:4243"));
            await repository.UpsertAsync(new GlobalSanction(4243, false, "spam",
                DateTimeOffset.UtcNow.AddDays(-2), "appealed", DateTimeOffset.UtcNow,
                "redis:globalBan:4243"));

            await using var connection = await database.OpenConnectionAsync();
            var row = await connection.QuerySingleAsync<(string Status, string Reason, string ResolutionReason)>(@"
SELECT g.status AS Status, g.reason AS Reason, g.resolution_reason AS ResolutionReason
  FROM global_sanctions g JOIN telegram_accounts a ON a.account_id = g.account_id
 WHERE a.telegram_user_id = 4243");

            Assert.Equal("revoked", row.Status);
            // The revocation used to overwrite reason, losing what the ban was for.
            Assert.Equal("spam", row.Reason);
            Assert.Equal("appealed", row.ResolutionReason);
        });
    }

    [PostgresFact]
    public async Task AnUnbanTimestampBeforeTheBanDoesNotViolateThePeriodConstraint()
    {
        await InSchemaAsync(async database =>
        {
            // The legacy timestamps come from at least four formats written by different hosts, so
            // ordering is not guaranteed. global_sanctions_period_check requires
            // resolved_at >= imposed_at, and a raw insert would fail the whole run.
            var imposed = new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero);
            await new GlobalSanctionRepository(database).UpsertAsync(new GlobalSanction(
                4244, false, "ads", imposed, "reversed", imposed.AddDays(-5), "redis:globalBan:4244"));

            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(imposed, await connection.ExecuteScalarAsync<DateTimeOffset>(@"
SELECT g.resolved_at FROM global_sanctions g
  JOIN telegram_accounts a ON a.account_id = g.account_id
 WHERE a.telegram_user_id = 4244"));
        });
    }

    private static async Task InSchemaAsync(Func<PostgresDatabase, Task> body)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_globalsanction_{Guid.NewGuid():N}";

        using var config = GlobalConfigScope.Acquire("StorageMode");
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            await body(database);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }
}
