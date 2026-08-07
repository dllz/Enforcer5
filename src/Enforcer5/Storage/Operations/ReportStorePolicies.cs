using System;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage.Moderation;

namespace Enforcer5.Storage.Operations
{
    internal static class ReportPolicyStore
    {
        internal static Task<bool> IsReporterBlockedAsync(long groupId, long userId) =>
            IsEnabledAsync(groupId, userId, GroupUserPolicyNames.ReportsBlocked,
                $"chat:{groupId}:reportblocked");

        internal static Task SetReporterBlockedAsync(long groupId, long userId, bool enabled) =>
            SetEnabledAsync(groupId, userId, GroupUserPolicyNames.ReportsBlocked,
                $"chat:{groupId}:reportblocked", enabled);

        internal static Task<bool> AreAdminNotificationsDisabledAsync(long groupId, long userId) =>
            IsEnabledAsync(groupId, userId, GroupUserPolicyNames.AdminNotificationsDisabled,
                $"chat:{groupId}:adminOff");

        internal static Task SetAdminNotificationsDisabledAsync(long groupId, long userId, bool enabled) =>
            SetEnabledAsync(groupId, userId, GroupUserPolicyNames.AdminNotificationsDisabled,
                $"chat:{groupId}:adminOff", enabled);

        private static async Task<bool> IsEnabledAsync(long groupId, long userId, string policy,
            string redisKey)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
                return await Redis.db.SetContainsAsync(redisKey, userId);
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<bool>(@"
SELECT COALESCE((SELECT enabled FROM group_user_policies
WHERE group_id = @GroupId AND user_id = @UserId AND policy = @Policy), FALSE)",
                new { GroupId = groupId, UserId = userId, Policy = policy });
        }

        private static async Task SetEnabledAsync(long groupId, long userId, string policy,
            string redisKey, bool enabled)
        {
            var shadow = string.Equals(RegHelper.GetRegValue("ModerationShadowEnabled"), "true",
                StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection"));
            if (!StorageModeProvider.PostgresIsPrimary)
            {
                await SetRedisAsync(redisKey, userId, enabled);
                if (!shadow) return;
            }

            await using var database = PostgresDatabase.FromConfiguration();
            await new GroupUserPolicyRepository(database).UpsertAsync(new GroupUserPolicy(groupId,
                userId, policy, enabled, DateTimeOffset.UtcNow));
            if (StorageModeProvider.RedisMirrorEnabled) await SetRedisAsync(redisKey, userId, enabled);
        }

        private static Task SetRedisAsync(string key, long userId, bool enabled) => enabled
            ? Redis.db.SetAddAsync(key, userId)
            : Redis.db.SetRemoveAsync(key, userId);
    }
}
