using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Enforcer5.Storage
{
    /// <summary>
    /// Brings the PostgreSQL schema up to date. Both editions run this at startup against the same
    /// database, so it holds an advisory lock; the second one waits and then finds nothing to do.
    /// </summary>
    internal static class SchemaMigrationRunner
    {
        private const long AdvisoryLockId = 0x454E464F52434552; // "ENFORCER"

        internal static async Task ApplyAsync(string connectionString,
            CancellationToken cancellationToken = default)
        {
            await using var context = EnforcerDbContextFactory.Create(connectionString);
            await context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_lock({AdvisoryLockId})", cancellationToken);
                await RefuseUntrackedExistingSchemaAsync(context.Database.GetDbConnection(), cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }
            finally
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_unlock({AdvisoryLockId})", CancellationToken.None);
                await context.Database.CloseConnectionAsync();
            }
        }

        /// <summary>
        /// A database that already holds Enforcer tables but no migration history was built some
        /// other way, e.g. by the full-migration rehearsals on feature/postgres-migration. Migrating
        /// it would guess at a baseline, so refuse and say so.
        /// </summary>
        private static async Task RefuseUntrackedExistingSchemaAsync(DbConnection connection,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    to_regclass(current_schema() || '.""__EFMigrationsHistory""') IS NOT NULL,
    to_regclass(current_schema() || '.users') IS NOT NULL OR
    to_regclass(current_schema() || '.telegram_accounts') IS NOT NULL OR
    to_regclass(current_schema() || '.groups') IS NOT NULL OR
    to_regclass(current_schema() || '.sanctions') IS NOT NULL OR
    to_regclass(current_schema() || '.group_settings') IS NOT NULL OR
    to_regclass(current_schema() || '.migration_runs') IS NOT NULL";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (!reader.GetBoolean(0) && reader.GetBoolean(1))
                throw new InvalidOperationException(
                    "PostgreSQL contains Enforcer tables without EF migration history. " +
                    "Refusing to guess a baseline; point PostgresConnection at an empty database.");
        }
    }
}
