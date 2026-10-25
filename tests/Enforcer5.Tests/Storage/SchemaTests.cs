using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace Enforcer5.Tests.Storage
{
    [TestClass]
    public class SchemaModelTests
    {
        private static EnforcerDbContext CreateContext() =>
            EnforcerDbContextFactory.Create("Host=localhost;Database=unused;Username=unused");

        [TestMethod]
        public void Model_HoldsOnlyTheTablesRolledOutSoFar()
        {
            using var context = CreateContext();
            var tables = context.Model.GetEntityTypes().Select(e => e.GetTableName()).OrderBy(t => t).ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "groups", "report_actions", "reports", "sanctions", "telegram_account_profiles", "telegram_accounts",
                "telegram_chat_profiles", "telegram_chats", "user_identity_links", "users",
                "warning_balances", "warning_events"
            }, tables);
        }

        [TestMethod]
        public void Migrations_MatchTheModel()
        {
            // Fails when the model was changed without adding a migration for it.
            using var context = CreateContext();
            Assert.IsFalse(context.Database.HasPendingModelChanges(),
                "The model has changes no migration covers: run dotnet ef migrations add.");
        }

        [TestMethod]
        public void Sanctions_UseTheApprovedKindsAndStatuses()
        {
            using var context = CreateContext();
            var model = context.GetService<IDesignTimeModel>().Model;
            var sanctions = model.FindEntityType(typeof(SanctionEntity));
            var checks = sanctions.GetCheckConstraints().ToDictionary(c => c.Name, c => c.Sql);
            Assert.AreEqual("kind IN ('ban', 'kick', 'mute', 'tempban', 'tempmute')", checks["sanctions_kind_check"]);
            Assert.AreEqual("status IN ('active', 'due', 'revoked', 'completed')", checks["sanctions_status_check"]);
            Assert.AreEqual("sanction_id", sanctions.FindProperty(nameof(SanctionEntity.SanctionId)).GetColumnName());
        }

        [TestMethod]
        public void Warnings_UseTheApprovedTypesAndOperations()
        {
            using var context = CreateContext();
            var model = context.GetService<IDesignTimeModel>().Model;
            var events = model.FindEntityType(typeof(WarningEventEntity)).GetCheckConstraints().ToDictionary(c => c.Name, c => c.Sql);
            Assert.AreEqual("warning_type IN ('standard', 'prewarning', 'media')", events["warning_events_type_check"]);
            Assert.AreEqual("operation IN ('add', 'remove', 'reset', 'decay')", events["warning_events_operation_check"]);
            var balances = model.FindEntityType(typeof(WarningBalanceEntity)).GetCheckConstraints().ToDictionary(c => c.Name, c => c.Sql);
            Assert.AreEqual("active_quantity >= 0 AND lifetime_quantity >= 0", balances["warning_balances_quantity_check"]);
        }
    }

    /// <summary>
    /// Runs against a real PostgreSQL when ENFORCER_TEST_POSTGRES holds a connection string, each
    /// test in a schema of its own that is dropped afterwards. Without it the tests are skipped
    /// (inconclusive), which is what happens in the Docker build: no database there.
    /// </summary>
    internal static class TestPostgres
    {
        internal const string ConnectionVariable = "ENFORCER_TEST_POSTGRES";

        internal static async Task<string> CreateSchemaAsync()
        {
            var baseConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
            if (string.IsNullOrWhiteSpace(baseConnection))
                Assert.Inconclusive($"Set {ConnectionVariable} to run the PostgreSQL tests.");
            var schema = $"enforcer_test_{Guid.NewGuid():N}";
            await using var admin = new NpgsqlConnection(Unpooled(baseConnection));
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
            return new NpgsqlConnectionStringBuilder(Unpooled(baseConnection)) { SearchPath = schema }.ConnectionString;
        }

        // Each schema is its own connection string and so its own pool, and pooled connections stay
        // open after the test. Subprod caps the role's connections, so tests do not pool.
        private static string Unpooled(string connectionString) =>
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

        internal static async Task DropSchemaAsync(string connectionString)
        {
            var schema = new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
            await using var admin = new NpgsqlConnection(Unpooled(Environment.GetEnvironmentVariable(ConnectionVariable)));
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    [TestClass]
    public class SchemaMigrationTests
    {
        [TestMethod]
        public async Task Apply_CreatesTheSchema_AndIsIdempotent()
        {
            var connectionString = await TestPostgres.CreateSchemaAsync();
            try
            {
                await SchemaMigrationRunner.ApplyAsync(connectionString);
                await SchemaMigrationRunner.ApplyAsync(connectionString);

                await using var database = new PostgresDatabase(connectionString);
                await using var connection = await database.OpenConnectionAsync();
                var schema = new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
                var tables = (await connection.QueryAsync<string>(
                    "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                    new { schema })).ToHashSet();
                Assert.IsTrue(tables.Contains("sanctions"));
                Assert.IsTrue(tables.Contains("telegram_accounts"));

                await using var context = EnforcerDbContextFactory.Create(connectionString);
                var applied = (await connection.QueryAsync<string>(
                    "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"")).ToHashSet();
                Assert.IsTrue(context.Database.GetMigrations().ToHashSet().SetEquals(applied));
            }
            finally
            {
                await TestPostgres.DropSchemaAsync(connectionString);
            }
        }

        [TestMethod]
        public async Task Apply_RefusesTablesWithoutMigrationHistory()
        {
            var connectionString = await TestPostgres.CreateSchemaAsync();
            try
            {
                await using (var database = new PostgresDatabase(connectionString))
                await using (var connection = await database.OpenConnectionAsync())
                    await connection.ExecuteAsync("CREATE TABLE sanctions (id int)");

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => SchemaMigrationRunner.ApplyAsync(connectionString));
            }
            finally
            {
                await TestPostgres.DropSchemaAsync(connectionString);
            }
        }
    }
}
