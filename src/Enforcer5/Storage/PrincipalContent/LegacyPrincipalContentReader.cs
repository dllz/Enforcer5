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
            return await ReadAsync(userIds, await DiscoverGroupIdsAsync(cancellationToken),
                cancellationToken);
        }

        /// <summary>
        /// The authoritative group population.
        /// <para>
        /// <c>bot:groupsid</c> is not complete: it holds 36,441 members while 38,152 groups have
        /// data in Redis. 7,951 groups have statistics and are absent from the set, so seeding a
        /// migration from it alone silently skips them. The set is unioned with the group ids
        /// discovered by scanning, and the scan is what makes the population trustworthy.
        /// </para>
        /// </summary>
        internal static async Task<IReadOnlyCollection<long>> DiscoverGroupIdsAsync(
            CancellationToken cancellationToken = default)
        {
            var groupIds = new HashSet<long>();

            foreach (var value in await Redis.db.SetMembersAsync("bot:groupsid"))
                if (long.TryParse(value.ToString(), out var id))
                    groupIds.Add(id);

            var declared = groupIds.Count;

            await foreach (var groupId in ScanGroupIdsAsync(cancellationToken))
                groupIds.Add(groupId);

            if (groupIds.Count != declared)
                LogHelper.Info($"Group discovery: bot:groupsid declared {declared}, " +
                               $"scan found {groupIds.Count - declared} further group(s) with data.");

            return groupIds;
        }

        /// <summary>
        /// Streams group ids out of the <c>{groupId}:users:{userId}</c> statistics keys with a
        /// cursor. This is the only pattern that reveals a group Redis knows about but
        /// <c>bot:groupsid</c> does not.
        /// </summary>
        private static async IAsyncEnumerable<long> ScanGroupIdsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var cursor = "0";
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = (RedisResult[])await Redis.db.ExecuteAsync("SCAN", cursor,
                    "MATCH", "*:users:*", "COUNT", 5000);
                if (result.Length != 2)
                    throw new InvalidOperationException("Redis returned a malformed SCAN response.");

                cursor = result[0].ToString();
                foreach (var entry in (RedisResult[])result[1])
                {
                    var key = entry.ToString();
                    var marker = key.IndexOf(":users:", StringComparison.Ordinal);
                    if (marker > 0 && long.TryParse(key.AsSpan(0, marker), out var groupId))
                        yield return groupId;
                }
            } while (!string.Equals(cursor, "0", StringComparison.Ordinal));
        }

        internal async Task<PrincipalContentBackfill> ReadAsync(
            IEnumerable<long> userIds, IEnumerable<long> groupIds,
            CancellationToken cancellationToken = default)
        {
            var users = new List<UserPrincipal>();
            var dataQualityIssues = new List<PrincipalDataQualityIssue>();
            foreach (var batch in userIds.Distinct().Chunk(500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reads = batch.Select(async userId => (UserId: userId, Values:
                    await Redis.db.HashGetAsync($"user:{userId}",
                        new RedisValue[] { "name", "username" }))).ToArray();
                foreach (var read in await Task.WhenAll(reads))
                {
                    // Every user:{id} key is a real user and is migrated.
                    //
                    // This used to keep a user only when name or username was present, which
                    // discarded 326,787 records - 6.38% of the keyspace - because their name had
                    // never been captured. Telegram always supplies first_name, so a missing name
                    // is a defective write upstream, not evidence that the record is not a user;
                    // 99.97% of the discarded records carried a legacy message counter, so they
                    // were demonstrably real. A missing username is not a defect at all: 40.6% of
                    // users legitimately have none, and it is never flagged.
                    var name = Value(read.Values[0]);
                    var username = Value(read.Values[1]);
                    users.Add(new UserPrincipal(read.UserId, name, username));

                    if (read.Values[0].HasValue) continue;
                    dataQualityIssues.Add(new PrincipalDataQualityIssue(
                        $"user:{read.UserId}", "name",
                        "Record has no name. Telegram always supplies first_name, so this is a " +
                        "defective legacy write; the user is migrated and the gap recorded."));
                }
            }

            var groups = new List<GroupPrincipal>();
            var entitlements = new List<GroupEntitlement>();
            var contentItems = new List<GroupContentItem>();
            var customCommands = new List<CustomCommand>();
            var customCommandConflicts = new List<CustomCommandConflictCandidate>();
            foreach (var chatId in groupIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                var parsedCommands = new List<(string SourceTrigger, CustomCommand Command)>();
                foreach (var command in commands)
                {
                    var sideKey = $"{commandKey}:{command.Name}";
                    var sideValues = await Redis.db.HashGetAsync(sideKey,
                        new RedisValue[] { "mediaid", "caption" });
                    parsedCommands.Add((command.Name.ToString(), LegacyPrincipalContentMapper.ParseCustomCommand(chatId,
                        command.Name.ToString(), command.Value.ToString(),
                        NullableValue(sideValues[0]), NullableValue(sideValues[1]))));
                }
                foreach (var group in parsedCommands.GroupBy(value => value.Command.Trigger,
                    StringComparer.Ordinal))
                {
                    if (group.Count() == 1)
                    {
                        customCommands.Add(group.Single().Command);
                        continue;
                    }
                    foreach (var candidate in group)
                        customCommandConflicts.Add(new CustomCommandConflictCandidate(Guid.NewGuid(), chatId,
                            candidate.Command.Trigger, candidate.SourceTrigger, candidate.Command.Content,
                            candidate.Command.MediaType, candidate.Command.FileId, candidate.Command.Caption,
                            candidate.Command.FileOwnerEdition));
                }
            }

            return new PrincipalContentBackfill(users, groups, entitlements, contentItems, customCommands,
                customCommandConflicts, dataQualityIssues);
        }

        private static string Value(RedisValue value) =>
            value.HasValue ? value.ToString() : null;

        private static string NullableValue(RedisValue value) =>
            value.IsNull ? null : value.ToString();
    }
}
