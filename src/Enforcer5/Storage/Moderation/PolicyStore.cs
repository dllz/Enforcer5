using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Moderation
{
    internal static class PolicyStore
    {
        internal static Task<bool> IsWatchDisabledAsync(long groupId, long userId) =>
            IsEnabledAsync(groupId, userId, GroupUserPolicyNames.WatchDisabled);

        internal static Task SetWatchDisabledAsync(long groupId, long userId, bool enabled) =>
            SetAsync(groupId, userId, GroupUserPolicyNames.WatchDisabled, enabled);

        internal static Task<bool> AreReportsBlockedAsync(long groupId, long userId) =>
            IsEnabledAsync(groupId, userId, GroupUserPolicyNames.ReportsBlocked);

        internal static Task SetReportsBlockedAsync(long groupId, long userId, bool enabled) =>
            SetAsync(groupId, userId, GroupUserPolicyNames.ReportsBlocked, enabled);

        internal static Task<bool> AreAdminNotificationsDisabledAsync(long groupId, long userId) =>
            IsEnabledAsync(groupId, userId, GroupUserPolicyNames.AdminNotificationsDisabled);

        internal static Task SetAdminNotificationsDisabledAsync(long groupId, long userId, bool enabled) =>
            SetAsync(groupId, userId, GroupUserPolicyNames.AdminNotificationsDisabled, enabled);

        internal static Task<bool> IsTagAllSubscribedAsync(long groupId, long userId) =>
            IsEnabledAsync(groupId, userId, GroupUserPolicyNames.TagAllSubscribed);

        internal static Task SetTagAllSubscribedAsync(long groupId, long userId, bool enabled) =>
            SetAsync(groupId, userId, GroupUserPolicyNames.TagAllSubscribed, enabled);

        internal static async Task<IReadOnlyList<long>> ListTagAllSubscribersAsync(long groupId)
        {
            var route = CurrentRoute(groupId);
            if (route.ReadRedis)
                return (await Redis.db.SetMembersAsync(RedisKey(groupId,
                        GroupUserPolicyNames.TagAllSubscribed)))
                    .Select(value => long.TryParse(value.ToString(), out var id) ? (long?)id : null)
                    .Where(id => id.HasValue).Select(id => id.Value).ToArray();

            await using var database = PostgresDatabase.FromConfiguration();
            return await new GroupUserPolicyRepository(database).ListEnabledUserIdsAsync(groupId,
                GroupUserPolicyNames.TagAllSubscribed);
        }

        internal static PolicyStoreRoute GetRoute(StorageMode mode, bool shadowEnabled) => mode switch
        {
            StorageMode.RedisPrimaryPostgresShadow => new PolicyStoreRoute(true, shadowEnabled, true),
            StorageMode.PostgresPrimaryRedisMirror => new PolicyStoreRoute(false, true, true),
            StorageMode.PostgresOnly => new PolicyStoreRoute(false, true, false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        private static async Task<bool> IsEnabledAsync(long groupId, long userId, string policy)
        {
            var route = CurrentRoute(groupId);
            if (route.ReadRedis)
                return await Redis.db.SetContainsAsync(RedisKey(groupId, policy), userId);

            await using var database = PostgresDatabase.FromConfiguration();
            return await new GroupUserPolicyRepository(database).IsEnabledAsync(groupId, userId, policy);
        }

        private static async Task SetAsync(long groupId, long userId, string policy, bool enabled)
        {
            var route = CurrentRoute(groupId);
            if (route.ReadRedis)
            {
                await SetRedisAsync(groupId, userId, policy, enabled);
                if (route.WritePostgres) await SetPostgresAsync(groupId, userId, policy, enabled);
                return;
            }

            await SetPostgresAsync(groupId, userId, policy, enabled);
            // PostgreSQL repository transaction enqueues the Redis rollback projection.
        }

        private static async Task SetPostgresAsync(long groupId, long userId, string policy, bool enabled)
        {
            await using var database = PostgresDatabase.FromConfiguration();
            await new GroupUserPolicyRepository(database).UpsertAsync(new GroupUserPolicy(groupId, userId,
                policy, enabled, DateTimeOffset.UtcNow));
        }

        private static async Task SetRedisAsync(long groupId, long userId, string policy, bool enabled)
        {
            var key = RedisKey(groupId, policy);
            if (enabled) await Redis.db.SetAddAsync(key, userId);
            else await Redis.db.SetRemoveAsync(key, userId);
            await Cutover.GroupDrainBuffer.AppendAsync(groupId, key);
        }

        private static PolicyStoreRoute CurrentRoute(long groupId) => GetRoute(StorageRouting.EffectiveGroupMode(groupId),
            string.Equals(RegHelper.GetRegValue("ModerationShadowEnabled"), "true",
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection")));

        private static string RedisKey(long groupId, string policy) => policy switch
        {
            GroupUserPolicyNames.WatchDisabled => $"chat:{groupId}:watch",
            GroupUserPolicyNames.ReportsBlocked => $"chat:{groupId}:reportblocked",
            GroupUserPolicyNames.AdminNotificationsDisabled => $"chat:{groupId}:adminOff",
            GroupUserPolicyNames.TagAllSubscribed => $"chat:{groupId}:tagall2",
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
    }

    internal readonly record struct PolicyStoreRoute(bool ReadRedis, bool WritePostgres, bool WriteRedis);
}
