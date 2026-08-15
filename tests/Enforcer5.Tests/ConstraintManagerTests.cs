using Dapper;
using Enforcer5.Storage;
using Enforcer5.Storage.Loading;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// Dropping foreign keys and secondary indexes is worth 2.5-2.8x on the user tier, which at 25M
/// rows decides whether the Stage B pause window is viable. It is also the most destructive thing
/// the loader does, so these cover both that it restores exactly what it removed and that a
/// failure puts everything back.
/// </summary>
public sealed class ConstraintManagerTests
{
    private static readonly string[] UserTierTables =
    [
        "users", "telegram_accounts", "user_identity_links", "telegram_account_profiles",
        "account_message_totals"
    ];

    [PostgresFact]
    public async Task DropAndRebuildRestoresEveryDefinitionExactly()
    {
        await InSchemaAsync(async database =>
        {
            var before = await DescribeAsync(database);
            Assert.NotEmpty(before);

            await using (var connection = await database.OpenConnectionAsync())
            {
                await using var transaction = await connection.BeginTransactionAsync();
                var dropped = await ConstraintManager.DropAsync(connection, transaction, UserTierTables);
                Assert.NotEmpty(dropped);

                // Nothing hardcoded: whatever the catalog holds is what gets dropped, so an EF
                // rename cannot silently leave constraints in place and halve the load rate.
                await ConstraintManager.RebuildAsync(connection, transaction, dropped);
                await transaction.CommitAsync();
            }

            Assert.Equal(before, await DescribeAsync(database));
        });
    }

    [PostgresFact]
    public async Task PrimaryKeysAndCheckConstraintsAreNeverDropped()
    {
        await InSchemaAsync(async database =>
        {
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var dropped = await ConstraintManager.DropAsync(connection, transaction, UserTierTables);

            // Primary keys back the ON CONFLICT clauses the merge depends on, and check constraints
            // are the last line of defence against loading data that violates an invariant.
            var survivors = (await connection.QueryAsync<string>(new CommandDefinition(@"
SELECT con.contype::text FROM pg_constraint con
  JOIN pg_class rel ON rel.oid = con.conrelid
  JOIN pg_namespace ns ON ns.oid = rel.relnamespace
 WHERE ns.nspname = current_schema() AND rel.relname = ANY(@Names)",
                new { Names = UserTierTables }, transaction))).ToList();

            Assert.Contains("p", survivors);
            Assert.Contains("c", survivors);
            Assert.DoesNotContain("f", survivors);
            Assert.All(dropped, x => Assert.NotEqual("PK_users", x.Name));

            await transaction.RollbackAsync();
        });
    }

    [PostgresFact]
    public async Task ARolledBackLoadLeavesTheConstraintsIntact()
    {
        await InSchemaAsync(async database =>
        {
            var before = await DescribeAsync(database);

            await using (var connection = await database.OpenConnectionAsync())
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await ConstraintManager.DropAsync(connection, transaction, UserTierTables);
                // Simulates the loader dying mid-merge. PostgreSQL DDL is transactional, so the
                // drops roll back with it. Dropping in a separate transaction would leave a live
                // database missing unique indexes with no record of what they were.
                await transaction.RollbackAsync();
            }

            Assert.Equal(before, await DescribeAsync(database));
        });
    }

    /// <summary>Every foreign key and index on the user tier, as the catalog sees it.</summary>
    private static async Task<List<string>> DescribeAsync(PostgresDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        var constraints = await connection.QueryAsync<string>(new CommandDefinition(@"
SELECT rel.relname || ' ' || con.conname || ' ' || pg_get_constraintdef(con.oid)
  FROM pg_constraint con
  JOIN pg_class rel ON rel.oid = con.conrelid
  JOIN pg_namespace ns ON ns.oid = rel.relnamespace
 WHERE ns.nspname = current_schema() AND rel.relname = ANY(@Names) AND con.contype = 'f'",
            new { Names = UserTierTables }));
        var indexes = await connection.QueryAsync<string>(new CommandDefinition(@"
SELECT rel.relname || ' ' || idx.relname || ' ' || pg_get_indexdef(ix.indexrelid)
  FROM pg_index ix
  JOIN pg_class idx ON idx.oid = ix.indexrelid
  JOIN pg_class rel ON rel.oid = ix.indrelid
  JOIN pg_namespace ns ON ns.oid = rel.relnamespace
 WHERE ns.nspname = current_schema() AND rel.relname = ANY(@Names)",
            new { Names = UserTierTables }));
        return constraints.Concat(indexes).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static async Task InSchemaAsync(Func<PostgresDatabase, Task> body)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_constraints_{Guid.NewGuid():N}";

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
