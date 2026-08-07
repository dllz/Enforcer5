using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;

namespace Enforcer5.Storage.Moderation
{
    internal sealed class LegacyModerationReader
    {
        internal async Task<ModerationSnapshot> ReadAsync(IEnumerable<long> knownGroupIds,
            CancellationToken cancellationToken = default)
        {
            var roles = new List<GroupRoleAssignment>();
            var blocks = new List<GroupElevationBlock>();
            var policies = new List<GroupUserPolicy>();
            var balances = new List<WarningBalance>();

            foreach (var groupId in knownGroupIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var authTask = Redis.db.SetMembersAsync($"chat:{groupId}:auth");
                var modTask = Redis.db.SetMembersAsync($"chat:{groupId}:mod");
                var deauthTask = Redis.db.SetMembersAsync($"chat:{groupId}:deauth");
                var watchTask = Redis.db.SetMembersAsync($"chat:{groupId}:watch");
                var reportBlockedTask = Redis.db.SetMembersAsync($"chat:{groupId}:reportblocked");
                var adminOffTask = Redis.db.SetMembersAsync($"chat:{groupId}:adminOff");
                var tagAllTask = Redis.db.SetMembersAsync($"chat:{groupId}:tagall2");
                var warnsTask = Redis.db.HashGetAllAsync($"chat:{groupId}:warns");
                var totalWarnsTask = Redis.db.HashGetAllAsync($"chat:{groupId}:totalWarns");
                var prewarnsTask = Redis.db.HashGetAllAsync($"chat:{groupId}:prewarns");
                var mediaWarnTask = Redis.db.HashGetAllAsync($"chat:{groupId}:mediawarn");
                await Task.WhenAll(authTask, modTask, deauthTask, watchTask, reportBlockedTask,
                    adminOffTask, tagAllTask, warnsTask, totalWarnsTask, prewarnsTask, mediaWarnTask);

                ValidateMembers(groupId, "auth", authTask.Result);
                ValidateMembers(groupId, "mod", modTask.Result);
                ValidateMembers(groupId, "deauth", deauthTask.Result);
                ValidateMembers(groupId, "watch", watchTask.Result);
                ValidateMembers(groupId, "reportblocked", reportBlockedTask.Result);
                ValidateMembers(groupId, "adminOff", adminOffTask.Result);
                ValidateMembers(groupId, "tagall2", tagAllTask.Result);
                ValidateWarningHash(groupId, "warns", warnsTask.Result);
                ValidateWarningHash(groupId, "totalWarns", totalWarnsTask.Result);
                ValidateWarningHash(groupId, "prewarns", prewarnsTask.Result);
                ValidateWarningHash(groupId, "mediawarn", mediaWarnTask.Result);

                var group = LegacyModerationMapper.MapGroup(groupId,
                    Values(authTask.Result), Values(modTask.Result), Values(deauthTask.Result),
                    Values(watchTask.Result), Values(reportBlockedTask.Result),
                    Values(adminOffTask.Result), Values(tagAllTask.Result),
                    Hash(warnsTask.Result), Hash(totalWarnsTask.Result), Hash(prewarnsTask.Result),
                    Hash(mediaWarnTask.Result), DateTimeOffset.UtcNow);
                roles.AddRange(group.Roles);
                blocks.AddRange(group.ElevationBlocks);
                policies.AddRange(group.Policies);
                balances.AddRange(group.WarningBalances);
            }

            return new ModerationSnapshot(roles, blocks, policies, balances);
        }

        private static IEnumerable<string> Values(IEnumerable<RedisValue> values) =>
            values.Select(value => value.ToString());

        private static IReadOnlyDictionary<string, string> Hash(IEnumerable<HashEntry> entries) =>
            entries.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString(),
                StringComparer.Ordinal);

        private static void ValidateMembers(long groupId, string key, IEnumerable<RedisValue> values)
        {
            foreach (var value in values)
                if (!long.TryParse(value.ToString(), out _))
                    throw new InvalidOperationException(
                        $"Malformed member '{value}' in chat:{groupId}:{key}; reconciliation aborted.");
        }

        private static void ValidateWarningHash(long groupId, string key, IEnumerable<HashEntry> entries)
        {
            foreach (var entry in entries)
                if (!long.TryParse(entry.Name.ToString(), out _) ||
                    !long.TryParse(entry.Value.ToString(), out var value) || value < 0)
                    throw new InvalidOperationException(
                        $"Malformed warning '{entry.Name}={entry.Value}' in chat:{groupId}:{key}; reconciliation aborted.");
        }
    }
}
