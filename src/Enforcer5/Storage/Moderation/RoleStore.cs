using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Moderation
{
    internal static class RoleStore
    {
        internal static Task<bool> IsAuthorizedAsync(long groupId, long userId) =>
            HasRoleAsync(groupId, userId, GroupRoles.Authorized);

        internal static Task<bool> IsModeratorAsync(long groupId, long userId) =>
            HasRoleAsync(groupId, userId, GroupRoles.Moderator);

        internal static Task AuthorizeAsync(long groupId, long userId) =>
            SetRoleAsync(groupId, userId, GroupRoles.Authorized, true);

        internal static Task DeauthorizeAsync(long groupId, long userId) =>
            SetRoleAsync(groupId, userId, GroupRoles.Authorized, false);

        internal static Task ElevateAsync(long groupId, long userId) =>
            SetRoleAsync(groupId, userId, GroupRoles.Moderator, true);

        internal static Task DeelevateAsync(long groupId, long userId) =>
            SetRoleAsync(groupId, userId, GroupRoles.Moderator, false);

        internal static async Task<IReadOnlyList<long>> ListModeratorsAsync(long groupId)
        {
            var route = CurrentRoute();
            if (route.ReadRedis)
                return (await Redis.db.SetMembersAsync($"chat:{groupId}:mod"))
                    .Select(value => long.TryParse(value.ToString(), out var id) ? (long?)id : null)
                    .Where(value => value.HasValue).Select(value => value.Value).ToArray();
            await using var database = PostgresDatabase.FromConfiguration();
            return (await new GroupRoleAssignmentRepository(database).ListActiveAsync(groupId))
                .Where(role => role.Role == GroupRoles.Moderator).Select(role => role.UserId).ToArray();
        }

        internal static async Task<bool> IsElevationBlockedAsync(long groupId, long userId)
        {
            var route = CurrentRoute();
            if (route.ReadRedis)
                return await Redis.db.SetContainsAsync(RedisKey(groupId, DurableRole.ElevationBlocked), userId);

            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1 FROM group_elevation_blocks
    WHERE group_id = @GroupId AND user_id = @UserId AND revoked_at IS NULL
)", new { GroupId = groupId, UserId = userId });
        }

        internal static Task BlockElevationAsync(long groupId, long userId) =>
            SetElevationBlockAsync(groupId, userId, true);

        internal static Task UnblockElevationAsync(long groupId, long userId) =>
            SetElevationBlockAsync(groupId, userId, false);

        internal static RoleStoreRoute GetRoute(StorageMode mode, bool shadowEnabled) => mode switch
        {
            StorageMode.RedisPrimaryPostgresShadow => new RoleStoreRoute(true, shadowEnabled, true),
            StorageMode.PostgresPrimaryRedisMirror => new RoleStoreRoute(false, true, true),
            StorageMode.PostgresOnly => new RoleStoreRoute(false, true, false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        private static async Task<bool> HasRoleAsync(long groupId, long userId, string role)
        {
            var route = CurrentRoute();
            if (route.ReadRedis)
                return await Redis.db.SetContainsAsync(RedisKey(groupId, role), userId);

            await using var database = PostgresDatabase.FromConfiguration();
            var roles = await new GroupRoleAssignmentRepository(database).ListActiveAsync(groupId);
            return roles.Any(assignment => assignment.UserId == userId && assignment.Role == role);
        }

        private static async Task SetRoleAsync(long groupId, long userId, string role, bool active)
        {
            var route = CurrentRoute();
            if (route.ReadRedis)
            {
                await SetRedisAsync(RedisKey(groupId, role), userId, active);
                if (route.WritePostgres) await SetPostgresRoleAsync(groupId, userId, role, active);
                return;
            }

            await SetPostgresRoleAsync(groupId, userId, role, active);
            // PostgreSQL repository transaction enqueues the Redis rollback projection.
        }

        private static async Task SetPostgresRoleAsync(long groupId, long userId, string role, bool active)
        {
            await using var database = PostgresDatabase.FromConfiguration();
            var repository = new GroupRoleAssignmentRepository(database);
            if (active)
                await repository.UpsertActiveAsync(new GroupRoleAssignment(groupId, userId, role,
                    DateTimeOffset.UtcNow, null));
            else
                await repository.RevokeAsync(groupId, userId, role);
        }

        private static async Task SetElevationBlockAsync(long groupId, long userId, bool active)
        {
            var route = CurrentRoute();
            if (route.ReadRedis)
            {
                await SetRedisAsync(RedisKey(groupId, DurableRole.ElevationBlocked), userId, active);
                if (route.WritePostgres) await SetPostgresElevationBlockAsync(groupId, userId, active);
                return;
            }

            await SetPostgresElevationBlockAsync(groupId, userId, active);
            // PostgreSQL repository transaction enqueues the Redis rollback projection.
        }

        private static async Task SetPostgresElevationBlockAsync(long groupId, long userId, bool active)
        {
            await using var database = PostgresDatabase.FromConfiguration();
            var repository = new GroupElevationBlockRepository(database);
            if (active)
                await repository.UpsertActiveAsync(new GroupElevationBlock(groupId, userId,
                    DateTimeOffset.UtcNow, null));
            else
                await repository.RevokeAsync(groupId, userId);
        }

        private static async Task SetRedisAsync(string key, long userId, bool active)
        {
            if (active) await Redis.db.SetAddAsync(key, userId);
            else await Redis.db.SetRemoveAsync(key, userId);
        }

        private static RoleStoreRoute CurrentRoute() => GetRoute(StorageModeProvider.Current,
            string.Equals(RegHelper.GetRegValue("ModerationShadowEnabled"), "true",
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection")));

        private static string RedisKey(long groupId, string role) => role switch
        {
            GroupRoles.Authorized => $"chat:{groupId}:auth",
            GroupRoles.Moderator => $"chat:{groupId}:mod",
            DurableRole.ElevationBlocked => $"chat:{groupId}:deauth",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        private static class DurableRole
        {
            internal const string ElevationBlocked = "elevation_blocked";
        }
    }

    internal readonly record struct RoleStoreRoute(bool ReadRedis, bool WritePostgres, bool WriteRedis);
}
