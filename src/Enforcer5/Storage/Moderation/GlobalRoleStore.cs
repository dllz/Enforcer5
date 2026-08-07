using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Moderation
{
    internal static class GlobalRoleStore
    {
        internal static async Task<bool> IsLanguageAdminAsync(long userId)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
                return await Redis.db.SetContainsAsync("langAdmins", userId);
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<bool>(@"
SELECT COALESCE((SELECT active FROM global_roles WHERE user_id = @UserId AND role = 'language_admin'), FALSE)",
                new { UserId = userId });
        }

        internal static async Task SetLanguageAdminAsync(long userId, bool active)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
            {
                if (active) await Redis.db.SetAddAsync("langAdmins", userId);
                else await Redis.db.SetRemoveAsync("langAdmins", userId);
                return;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO global_roles (user_id, role, active, updated_at)
VALUES (@UserId, 'language_admin', @Active, now())
ON CONFLICT (user_id, role) DO UPDATE SET active = EXCLUDED.active, updated_at = now()",
                new { UserId = userId, Active = active }, transaction);
            if (StorageModeProvider.RedisMirrorEnabled)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "global-roles",
                    $"global-role:{userId}:language_admin", "set",
                    new { userId, role = "language_admin", active });
            await transaction.CommitAsync();
        }
    }
}
