using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Storage.Moderation
{
    internal static class GlobalRoleStore
    {
        private const string LanguageAdminRole = "global_admin";

        internal static async Task<bool> IsLanguageAdminAsync(long userId)
        {
            if (!StorageRouting.GlobalTierPostgresIsPrimary)
                return await Redis.db.SetContainsAsync("langAdmins", userId);
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, userId);
            var active = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM global_role_assignments
WHERE user_id = @UserId AND role = @Role AND revoked_at IS NULL)",
                new { UserId = user.UserId, Role = LanguageAdminRole }, transaction);
            await transaction.CommitAsync();
            return active;
        }

        internal static async Task SetLanguageAdminAsync(long userId, bool active)
        {
            if (!StorageRouting.GlobalTierPostgresIsPrimary)
            {
                if (active) await Redis.db.SetAddAsync("langAdmins", userId);
                else await Redis.db.SetRemoveAsync("langAdmins", userId);
                return;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, userId);
            if (active)
                await connection.ExecuteAsync(@"
INSERT INTO global_role_assignments (user_id, role, assigned_at)
VALUES (@UserId, @Role, now())
ON CONFLICT (user_id, role) WHERE revoked_at IS NULL DO NOTHING",
                    new { UserId = user.UserId, Role = LanguageAdminRole }, transaction);
            else
                await connection.ExecuteAsync(@"
UPDATE global_role_assignments SET revoked_at = now()
WHERE user_id = @UserId AND role = @Role AND revoked_at IS NULL",
                    new { UserId = user.UserId, Role = LanguageAdminRole }, transaction);
            if (StorageRouting.RedisMirrorEnabled)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "global-roles",
                    $"global-role:{userId}:language_admin", "set",
                    new { userId, role = LanguageAdminRole, active });
            await transaction.CommitAsync();
        }
    }
}
