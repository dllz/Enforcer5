using Dapper;
using Enforcer5.Storage;
using Enforcer5.Storage.Loading;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The set-based resolver replaces four to six round trips and an advisory lock per row. At 7.38M
/// group statistics rows the per-row version is over ten million round trips before a single
/// counter is written, which is why the operations domain has never completed a run.
/// </summary>
public sealed class BulkPrincipalResolverTests
{
    [PostgresFact]
    public async Task EveryMissingUserGetsAFullyLinkedPrincipal()
    {
        await InSchemaAsync(async database =>
        {
            await StageUsersAsync(database, [11, 22, 33]);

            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var created = await BulkPrincipalResolver.EnsureAccountsAsync(connection, transaction,
                "SELECT telegram_user_id FROM migration_stage_users");
            await transaction.CommitAsync();

            Assert.Equal(3, created);
            // One Telegram user becomes a users row, a telegram_accounts row and the link between
            // them. A partial set would leave an account no query can reach through its user.
            Assert.Equal(3, await connection.ExecuteScalarAsync<long>(@"
SELECT count(*) FROM telegram_accounts a
  JOIN user_identity_links l ON l.account_id = a.account_id AND l.unlinked_at IS NULL
  JOIN users u ON u.user_id = l.user_id
 WHERE a.telegram_user_id = ANY(ARRAY[11,22,33])"));
        });
    }

    [PostgresFact]
    public async Task RerunningCreatesNothingBecauseTheSecondPassFindsNothingMissing()
    {
        await InSchemaAsync(async database =>
        {
            await StageUsersAsync(database, [11, 22]);

            await using var connection = await database.OpenConnectionAsync();
            await using (var first = await connection.BeginTransactionAsync())
            {
                Assert.Equal(2, await BulkPrincipalResolver.EnsureAccountsAsync(connection, first,
                    "SELECT telegram_user_id FROM migration_stage_users"));
                await first.CommitAsync();
            }
            await using (var second = await connection.BeginTransactionAsync())
            {
                // A re-run after a partial failure must converge, not duplicate.
                Assert.Equal(0, await BulkPrincipalResolver.EnsureAccountsAsync(connection, second,
                    "SELECT telegram_user_id FROM migration_stage_users"));
                await second.CommitAsync();
            }

            Assert.Equal(2, await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM telegram_accounts"));
        });
    }

    [PostgresFact]
    public async Task TheRuntimeResolverStillGetsFreshIdsAfterABlockIsReserved()
    {
        await InSchemaAsync(async database =>
        {
            await StageUsersAsync(database, [11, 22, 33]);

            await using var connection = await database.OpenConnectionAsync();
            await using (var load = await connection.BeginTransactionAsync())
            {
                await BulkPrincipalResolver.EnsureAccountsAsync(connection, load,
                    "SELECT telegram_user_id FROM migration_stage_users");
                await load.CommitAsync();
            }

            // The load claims a contiguous identity block by advancing the sequence. If it advanced
            // it incorrectly the next runtime insert would collide with a row the load just wrote -
            // and both columns are IDENTITY ALWAYS, so the collision surfaces as a constraint
            // violation on the message hot path rather than at load time.
            await using var runtime = await connection.BeginTransactionAsync();
            var resolved = await PrincipalResolver.EnsureUserAsync(connection, runtime, 44);
            await runtime.CommitAsync();

            Assert.Equal(4, await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM telegram_accounts"));
            Assert.Equal(44, await connection.ExecuteScalarAsync<long>(
                "SELECT telegram_user_id FROM telegram_accounts WHERE account_id = @AccountId",
                new { resolved.AccountId }));
        });
    }

    [PostgresFact]
    public async Task GroupsGetBothAChatAndAGroupRow()
    {
        await InSchemaAsync(async database =>
        {
            await using var seed = await database.OpenConnectionAsync();
            await seed.ExecuteAsync(@"
CREATE UNLOGGED TABLE migration_stage_group_members (
    telegram_chat_id bigint NOT NULL, telegram_user_id bigint NOT NULL, messages bigint);
INSERT INTO migration_stage_group_members VALUES (-100100, 11, 5), (-100200, 22, 7)");

            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var created = await BulkPrincipalResolver.EnsureGroupsAsync(connection, transaction,
                "SELECT telegram_chat_id FROM migration_stage_group_members");
            await transaction.CommitAsync();

            Assert.Equal(2, created);
            Assert.Equal(2, await connection.ExecuteScalarAsync<long>(@"
SELECT count(*) FROM groups g JOIN telegram_chats c ON c.chat_id = g.chat_id
 WHERE c.telegram_chat_id = ANY(ARRAY[-100100,-100200])"));
            // Chat type is recorded as unknown rather than guessed: the legacy data does not carry
            // it, and EnsureChatAsync treats unknown as "do not overwrite" so Telegram corrects it.
            Assert.Equal("unknown", await connection.ExecuteScalarAsync<string>(
                "SELECT chat_type FROM telegram_chats WHERE telegram_chat_id = -100100"));
        });
    }

    private static async Task StageUsersAsync(PostgresDatabase database, long[] ids)
    {
        await using var connection = await database.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "CREATE UNLOGGED TABLE migration_stage_users (telegram_user_id bigint NOT NULL)");
        foreach (var id in ids)
            await connection.ExecuteAsync(
                "INSERT INTO migration_stage_users VALUES (@Id)", new { Id = id });
    }

    private static async Task InSchemaAsync(Func<PostgresDatabase, Task> body)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_bulkprincipal_{Guid.NewGuid():N}";

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
