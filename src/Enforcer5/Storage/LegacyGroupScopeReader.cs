using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Enforcer5.Storage
{
    internal sealed record LegacyGroupScope(long GroupId, IReadOnlyCollection<long> UserIds,
        IReadOnlyCollection<long> ReportMessageIds);

    internal sealed class LegacyGroupScopeReader
    {
        private const int ScanPageSize = 250;

        private static readonly string[] ModerationSets =
        {
            "auth", "mod", "deauth", "watch", "reportblocked", "adminOff", "tagall2"
        };

        private static readonly string[] ModerationHashes =
        {
            "warns", "totalWarns", "prewarns", "mediawarn"
        };

        private static readonly string[] SanctionSets =
        {
            "banned", "bannedlist", "muted", "mutedJoiners", "tempbanned", "tempbannedPremium"
        };

        private static readonly string[] SanctionHashes = { "tempmuted" };
        private static readonly string[] ReportUserFields =
        {
            "ReporterUserId", "ReportedUserId", "SolvedByUserId"
        };

        private readonly IDatabase _database;

        internal LegacyGroupScopeReader(IDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal async Task<LegacyGroupScope> ReadAsync(long groupId, bool includeReports = true,
            CancellationToken cancellationToken = default)
        {
            if (groupId == 0) throw new ArgumentOutOfRangeException(nameof(groupId), "Group id must be nonzero.");
            // Deliberately not gated on bot:groupsid membership. That set holds 36,441 members while
            // 38,152 groups have data, because it was only ever written from one of the three code
            // paths that can create a settings hash. Requiring membership made this throw for all
            // 7,951 orphans - 21% of the estate - so a per-group cutover could never reach them and
            // a --group migration of one simply failed. The keys read below are the evidence that a
            // group exists; the registry is not.

            var userIds = new HashSet<long>();
            var reportMessageIds = new HashSet<long>();

            foreach (var suffix in ModerationSets)
                AddValues(await _database.SetMembersAsync($"chat:{groupId}:{suffix}"),
                    $"chat:{groupId}:{suffix}", userIds);
            foreach (var suffix in ModerationHashes)
                AddHashNames(await _database.HashGetAllAsync($"chat:{groupId}:{suffix}"),
                    $"chat:{groupId}:{suffix}", userIds);
            foreach (var suffix in SanctionSets)
                await AddKeyIdsAsync($"chat:{groupId}:{suffix}", userIds);
            foreach (var suffix in SanctionHashes)
                AddHashNames(await _database.HashGetAllAsync($"chat:{groupId}:{suffix}"),
                    $"chat:{groupId}:{suffix}", userIds);

            AddHashNames(await _database.HashGetAllAsync($"chat:{groupId}:userlast"),
                $"chat:{groupId}:userlast", userIds);

            if (includeReports)
            await foreach (var key in ScanKeysAsync($"flagged:{groupId}:*", cancellationToken))
            {
                var prefix = $"flagged:{groupId}:";
                var messageId = ParseId(key[prefix.Length..], key);
                reportMessageIds.Add(messageId);

                var fields = await _database.HashGetAsync(key,
                    Array.ConvertAll(ReportUserFields, field => (RedisValue)field));
                for (var i = 0; i < fields.Length; i++)
                {
                    if (!fields[i].HasValue) continue;
                    var allowZero = string.Equals(ReportUserFields[i], "ReportedUserId",
                        StringComparison.Ordinal);
                    var value = ParseId(fields[i].ToString(),
                        $"{key} field '{ReportUserFields[i]}'", allowZero);
                    if (value != 0) userIds.Add(value);
                }
            }

            return new LegacyGroupScope(groupId, userIds, reportMessageIds);
        }

        private async Task AddKeyIdsAsync(string key, ISet<long> ids)
        {
            switch (await _database.KeyTypeAsync(key))
            {
                case RedisType.None:
                    return;
                case RedisType.Set:
                    AddValues(await _database.SetMembersAsync(key), key, ids);
                    return;
                case RedisType.Hash:
                    AddHashNames(await _database.HashGetAllAsync(key), key, ids);
                    return;
                case RedisType.String:
                    var value = await _database.StringGetAsync(key);
                    if (value.HasValue) AddId(value.ToString(), key, ids);
                    return;
                default:
                    throw new FormatException($"Redis selected-scope key '{key}' has unsupported type.");
            }
        }

        private async IAsyncEnumerable<string> ScanKeysAsync(string pattern,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var cursor = "0";
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = (RedisResult[])await _database.ExecuteAsync("SCAN", cursor,
                    "MATCH", pattern, "COUNT", ScanPageSize);
                if (result.Length != 2)
                    throw new InvalidOperationException("Redis returned a malformed SCAN response.");

                cursor = result[0].ToString();
                foreach (var key in (RedisResult[])result[1]) yield return key.ToString();
            } while (!string.Equals(cursor, "0", StringComparison.Ordinal));
        }

        private static void AddValues(IEnumerable<RedisValue> values, string key, ISet<long> ids)
        {
            foreach (var value in values) AddId(value.ToString(), key, ids);
        }

        private static void AddHashNames(IEnumerable<HashEntry> entries, string key, ISet<long> ids)
        {
            foreach (var entry in entries) AddId(entry.Name.ToString(), key, ids);
        }

        private static void AddId(string value, string key, ISet<long> ids) => ids.Add(ParseId(value, key));

        private static long ParseId(string value, string key, bool allowZero = false)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                (!allowZero && id == 0))
                throw new FormatException($"Redis selected-scope id '{value}' in '{key}' is malformed.");
            return id;
        }
    }
}
