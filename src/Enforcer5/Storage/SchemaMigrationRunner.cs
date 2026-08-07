using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;
using System.Data.Common;

namespace Enforcer5.Storage
{
    internal static class SchemaMigrationRunner
    {
        private const long AdvisoryLockId = 0x454E464F52434552;

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

        private static async Task RefuseUntrackedExistingSchemaAsync(DbConnection connection,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    to_regclass(current_schema() || '.""__EFMigrationsHistory""') IS NOT NULL,
    to_regclass(current_schema() || '.group_settings') IS NOT NULL OR
    to_regclass(current_schema() || '.migration_runs') IS NOT NULL OR
    to_regclass(current_schema() || '.users') IS NOT NULL OR
    to_regclass(current_schema() || '.groups') IS NOT NULL OR
    to_regclass(current_schema() || '.group_content_items') IS NOT NULL OR
    to_regclass(current_schema() || '.custom_commands') IS NOT NULL OR
    to_regclass(current_schema() || '.group_entitlements') IS NOT NULL";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (!reader.GetBoolean(0) && reader.GetBoolean(1))
                throw new InvalidOperationException(
                    "PostgreSQL contains Enforcer tables without EF migration history. " +
                    "Refusing to guess a baseline; migrate a verified empty database or provide an explicit baseline procedure.");
        }
    }
}
