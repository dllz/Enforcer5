using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;

namespace Enforcer5.Storage.PrincipalContent
{
    internal sealed class LegacyPrincipalContentReader
    {
        internal async Task<PrincipalContentBackfill> ReadAsync(
            IEnumerable<long> userIds, CancellationToken cancellationToken = default)
        {
            var users = new List<UserPrincipal>();
            foreach (var userId in userIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = await Redis.db.HashGetAsync($"user:{userId}",
                    new RedisValue[] { "name", "username" });
                if (values[0].HasValue || values[1].HasValue)
                    users.Add(new UserPrincipal(userId, Value(values[0]), Value(values[1])));
            }

            var groups = new List<GroupPrincipal>();
            var entitlements = new List<GroupEntitlement>();
            var contentItems = new List<GroupContentItem>();
            var customCommands = new List<CustomCommand>();
            var groupValues = await Redis.db.SetMembersAsync("bot:groupsid");
            foreach (var groupValue in groupValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!long.TryParse(groupValue.ToString(), out var chatId)) continue;

                var name = await Redis.db.HashGetAsync($"chat:{chatId}:details", "name");
                groups.Add(new GroupPrincipal(chatId, Value(name)));

                if (await Redis.db.SetContainsAsync("premiumBot", chatId))
                    entitlements.Add(new GroupEntitlement(chatId, "premium", true));

                var rulesTask = Redis.db.StringGetAsync($"chat:{chatId}:rules");
                var aboutTask = Redis.db.StringGetAsync($"chat:{chatId}:about");
                var languageTask = Redis.db.StringGetAsync($"chat:{chatId}:language");
                var linkTask = Redis.db.HashGetAsync($"chat:{chatId}links", "link");
                await Task.WhenAll(rulesTask, aboutTask, languageTask, linkTask);
                contentItems.AddRange(LegacyPrincipalContentMapper.MapContent(chatId,
                    NullableValue(rulesTask.Result), NullableValue(aboutTask.Result),
                    NullableValue(languageTask.Result), NullableValue(linkTask.Result)));

                var commandKey = $"chat:{chatId}:extra";
                var commands = await Redis.db.HashGetAllAsync(commandKey);
                foreach (var command in commands)
                {
                    var sideKey = $"{commandKey}:{command.Name}";
                    var sideValues = await Redis.db.HashGetAsync(sideKey,
                        new RedisValue[] { "mediaid", "caption" });
                    customCommands.Add(LegacyPrincipalContentMapper.ParseCustomCommand(chatId,
                        command.Name.ToString(), command.Value.ToString(),
                        NullableValue(sideValues[0]), NullableValue(sideValues[1])));
                }
            }

            return new PrincipalContentBackfill(users, groups, entitlements, contentItems, customCommands);
        }

        private static string Value(RedisValue value) =>
            value.HasValue ? value.ToString() : null;

        private static string NullableValue(RedisValue value) =>
            value.IsNull ? null : value.ToString();
    }
}
