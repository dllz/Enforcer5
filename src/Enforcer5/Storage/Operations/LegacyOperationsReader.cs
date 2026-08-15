using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;

namespace Enforcer5.Storage.Operations
{
    internal sealed class LegacyOperationsReader
    {
        internal async Task<OperationsBackfillSnapshot> ReadAsync(IEnumerable<long> knownGroupIds,
            IEnumerable<long> knownUserIds, IReadOnlyDictionary<long, IReadOnlyCollection<long>> reportMessageIds,
            CancellationToken cancellationToken = default, bool includeGlobalData = true)
        {
            var snapshot = new OperationsBackfillSnapshot();
            var capturedAt = DateTimeOffset.UtcNow;
            var groupIds = knownGroupIds.Distinct().ToHashSet();
            var userIds = knownUserIds.Distinct().ToArray();
            await ReadTempBansAsync("tempbanned", Editions.Normal, groupIds, snapshot, capturedAt, cancellationToken);
            await ReadTempBansAsync("tempbannedPremium", Editions.Premium, groupIds, snapshot, capturedAt, cancellationToken);
            if (includeGlobalData) await ReadBotBaselineAsync(OperationsStore.CurrentEdition, snapshot, capturedAt);

            if (includeGlobalData) foreach (var batch in userIds.Chunk(1000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reads = batch.Select(async userId =>
                {
                    var values = await Task.WhenAll(
                        Redis.db.HashGetAllAsync($"globalBan:{userId}"),
                        Redis.db.HashGetAsync($"chat:{userId}", "msgs").ContinueWith(value =>
                            new[] { new HashEntry("msgs", value.Result) }, cancellationToken),
                        // ban:{userId} is user-tier and had no migration path at all before this,
                        // so 121,061 keys of moderation history would have been left behind.
                        Redis.db.HashGetAllAsync($"ban:{userId}"));
                    return (UserId: userId, Global: values[0], Messages: values[1][0].Value,
                        Moderation: values[2]);
                }).ToArray();
                foreach (var read in await Task.WhenAll(reads))
                {
                    ReadGlobalSanction(read.UserId, read.Global, snapshot);
                    ReadUserStatistics(read.UserId, read.Messages, snapshot, capturedAt);
                    ReadModerationCounters(read.UserId, read.Moderation, snapshot);
                }
            }
            var statistics = await ReadExistingGroupStatisticsAsync(groupIds, snapshot, capturedAt,
                cancellationToken);
            foreach (var groupId in groupIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadGroupAsync(groupId, statistics, snapshot, capturedAt);
                if (reportMessageIds != null && reportMessageIds.TryGetValue(groupId, out var ids))
                    foreach (var messageId in ids.Distinct()) await ReadReportAsync(groupId, messageId, snapshot);
            }
            return snapshot;
        }

        private static async Task ReadTempBansAsync(string hash, string edition,
            IReadOnlySet<long> groupIds, OperationsBackfillSnapshot snapshot,
            DateTimeOffset capturedAt, CancellationToken token)
        {
            foreach (var entry in await Redis.db.HashGetAllAsync(hash))
            {
                token.ThrowIfCancellationRequested();
                var parsed = LegacyOperationParsers.ParseTempBan(entry.Name.ToString(), entry.Value.ToString(), edition);
                if (!parsed.IsSuccess) { snapshot.Errors.Add(WithKey(hash, entry.Name, parsed.Error)); continue; }
                var value = parsed.Value;
                if (!groupIds.Contains(value.GroupId)) continue;
                snapshot.Sanctions.Add(new Sanction(0, value.GroupId, value.UserId,
                    SanctionKinds.TempBan, SanctionStatuses.Active,
                    edition, null, null, capturedAt, value.ExpiresAt, null, null, null,
                    $"redis:{hash}:{entry.Name}"));
                snapshot.AuditEvents.Add(new AuditEvent(0, edition, "legacy_tempban", value.GroupId,
                    null, value.UserId, entry.Value.ToString(), capturedAt,
                    $"redis:audit:{hash}:{entry.Name}"));
            }
        }

        private static void ReadGlobalSanction(long userId, HashEntry[] entries,
            OperationsBackfillSnapshot snapshot)
        {
            var key = $"globalBan:{userId}";
            if (entries.Length == 0) return;
            var parsed = LegacyOperationParsers.ParseGlobalBan(userId, Hash(entries), $"redis:{key}");
            if (parsed.Value != null) snapshot.GlobalSanctions.Add(parsed.Value);
            if (parsed.Value != null)
                snapshot.AuditEvents.Add(new AuditEvent(0, Editions.Shared, "legacy_global_sanction",
                    null, null, userId, parsed.Value.Reason, parsed.Value.CreatedAt ?? DateTimeOffset.UnixEpoch,
                    $"redis:audit:{key}"));
            if (!parsed.IsSuccess) snapshot.Errors.Add(WithKey(key, null, parsed.Error));
        }

        /// <summary>
        /// Absolute resulting values are carried, never increments - replaying an increment is not
        /// idempotent, so a re-run would double-count.
        /// </summary>
        private static void ReadModerationCounters(long userId, HashEntry[] entries,
            OperationsBackfillSnapshot snapshot)
        {
            if (entries == null) return;
            foreach (var entry in entries)
            {
                var kind = entry.Name.ToString();
                if (string.IsNullOrWhiteSpace(kind)) continue;
                if (!TryNonNegativeInt64(entry.Value, out var count))
                {
                    snapshot.Errors.Add(WithKey($"ban:{userId}", kind,
                        new FormatException("Moderation counter is not a non-negative integer.")));
                    continue;
                }
                snapshot.ModerationCounters.Add(new AccountModerationCounter(userId, kind, count));
            }
        }

        private static void ReadUserStatistics(long userId, RedisValue value,
            OperationsBackfillSnapshot snapshot,
            DateTimeOffset capturedAt)
        {
            if (!value.HasValue) return;
            if (TryNonNegativeInt64(value, out var messages))
                snapshot.UserStatistics.Add(new UserStatistic(userId, messages, capturedAt));
            else snapshot.Errors.Add(WithKey($"chat:{userId}", "msgs", new FormatException("Message count is not a non-negative integer.")));
        }

        private static async Task ReadGroupAsync(long groupId,
            IReadOnlyDictionary<long, Dictionary<long, long>> statistics,
            OperationsBackfillSnapshot snapshot, DateTimeOffset capturedAt)
        {
            var bannedKey = $"chat:{groupId}:bannedlist";
            var banned = await ReadUserIdsAsync(bannedKey);
            foreach (var member in banned)
            {
                var userId = member;
                var details = Hash(await Redis.db.HashGetAllAsync($"chat:{groupId}:bannedlist:{userId}"));
                snapshot.Sanctions.Add(new Sanction(0, groupId, userId, SanctionKinds.Ban,
                    SanctionStatuses.Active, Editions.Normal, null, Get(details, "why"), capturedAt,
                    null, null, null, null, $"redis:chat:{groupId}:bannedlist:{userId}"));
                snapshot.AuditEvents.Add(new AuditEvent(0, Editions.Normal, "legacy_ban", groupId,
                    null, userId, Get(details, "why"), capturedAt,
                    $"redis:audit:chat:{groupId}:bannedlist:{userId}"));
            }

            foreach (var entry in await Redis.db.HashGetAllAsync($"chat:{groupId}:tempmuted"))
            {
                if (!long.TryParse(entry.Name.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId) ||
                    !long.TryParse(entry.Value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                {
                    snapshot.Errors.Add(WithKey($"chat:{groupId}:tempmuted", entry.Name,
                        new FormatException("Tempmute user or expiry is invalid.")));
                    continue;
                }
                try
                {
                    var expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    snapshot.Sanctions.Add(new Sanction(0, groupId, userId, SanctionKinds.TempMute,
                        SanctionStatuses.Active,
                        Editions.Normal, null, null, capturedAt, expires, null, null, null,
                        $"redis:chat:{groupId}:tempmuted:{userId}"));
                    snapshot.AuditEvents.Add(new AuditEvent(0, Editions.Normal, "legacy_tempmute", groupId,
                        null, userId, null, capturedAt, $"redis:audit:chat:{groupId}:tempmuted:{userId}"));
                }
                catch (ArgumentOutOfRangeException e) { snapshot.Errors.Add(WithKey($"chat:{groupId}:tempmuted", entry.Name, e)); }
            }

            var last = Hash(await Redis.db.HashGetAllAsync($"chat:{groupId}:userlast"));
            var users = statistics.TryGetValue(groupId, out var counts)
                ? counts.Keys.Concat(last.Keys.Select(value => long.TryParse(value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0))
                    .Where(value => value != 0).Distinct()
                : last.Keys.Select(value => long.TryParse(value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id) ? id : 0).Where(value => value != 0);
            foreach (var userId in users)
            {
                var messages = counts != null && counts.TryGetValue(userId, out var count) ? count : 0L;
                DateTimeOffset? lastAt = null;
                if (last.TryGetValue(userId.ToString(CultureInfo.InvariantCulture), out var ticks))
                {
                    var parsed = LegacyOperationParsers.ParseTicks(ticks);
                    if (!parsed.IsSuccess) snapshot.Errors.Add(WithKey($"chat:{groupId}:userlast", userId, parsed.Error));
                    else lastAt = parsed.Value;
                }
                snapshot.GroupUserStatistics.Add(new GroupUserStatistic(groupId, userId, messages, lastAt, capturedAt));
            }
        }

        private static async Task<IReadOnlyDictionary<long, Dictionary<long, long>>>
            ReadExistingGroupStatisticsAsync(IReadOnlySet<long> groupIds, OperationsBackfillSnapshot snapshot,
                DateTimeOffset capturedAt, CancellationToken cancellationToken)
        {
            var result = new Dictionary<long, Dictionary<long, long>>();
            var cursor = "0";
            const string script = @"
local scan = redis.call('SCAN', ARGV[1], 'MATCH', '*:users:*', 'COUNT', 5000)
local values = {}
for _, key in ipairs(scan[2]) do
  local count = redis.call('HGET', key, 'msgs')
  if count then table.insert(values, key); table.insert(values, count) end
end
return {scan[1], values}";
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = (RedisResult[])await Redis.db.ScriptEvaluateAsync(script,
                    values: new RedisValue[] { cursor });
                cursor = response[0].ToString();
                var values = (RedisResult[])response[1];
                for (var i = 0; i + 1 < values.Length; i += 2)
                {
                    var key = values[i].ToString();
                    var first = key.IndexOf(":users:", StringComparison.Ordinal);
                    if (first <= 0 || !long.TryParse(key.AsSpan(0, first), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var groupId) || !groupIds.Contains(groupId) ||
                        !long.TryParse(key.AsSpan(first + 7), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var userId)) continue;
                    if (!long.TryParse(values[i + 1].ToString(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var count) || count < 0)
                    {
                        snapshot.Errors.Add(WithKey(key, "msgs",
                            new FormatException("Message count is not a non-negative integer.")));
                        continue;
                    }
                    if (!result.TryGetValue(groupId, out var group))
                        result[groupId] = group = new Dictionary<long, long>();
                    group[userId] = count;
                }
            } while (!string.Equals(cursor, "0", StringComparison.Ordinal));
            return result;
        }

        private static async Task<IReadOnlyCollection<long>> ReadUserIdsAsync(string key)
        {
            IEnumerable<RedisValue> values = (await Redis.db.KeyTypeAsync(key)) switch
            {
                RedisType.None => Array.Empty<RedisValue>(),
                RedisType.Set => await Redis.db.SetMembersAsync(key),
                RedisType.String => new[] { await Redis.db.StringGetAsync(key) },
                RedisType.Hash => (await Redis.db.HashKeysAsync(key)).AsEnumerable(),
                _ => throw new FormatException($"Redis key '{key}' has an unsupported user-list type.")
            };
            var result = new List<long>();
            foreach (var value in values)
            {
                if (!value.HasValue) continue;
                if (!long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var userId) || userId == 0)
                    throw new FormatException($"Redis key '{key}' contains invalid user id '{value}'.");
                result.Add(userId);
            }
            return result;
        }

        private static async Task ReadReportAsync(long groupId, long messageId,
            OperationsBackfillSnapshot snapshot)
        {
            var key = $"flagged:{groupId}:{messageId}";
            var fields = Hash(await Redis.db.HashGetAllAsync(key));
            if (fields.Count == 0) return;
            // Some old delivery-only hashes outlived their report metadata. They remain Redis
            // synchronization debris, not durable report history.
            if (!fields.ContainsKey("Solved")) return;
            var parsed = LegacyOperationParsers.ParseReport(groupId, messageId, fields, $"redis:{key}");
            if (parsed.Value == null) { snapshot.Errors.Add(WithKey(key, null, parsed.Error)); return; }
            var temporaryId = -(snapshot.Reports.Count + 1L);
            snapshot.Reports.Add(parsed.Value with { Id = temporaryId });
            snapshot.AuditEvents.Add(new AuditEvent(0, Editions.Shared, "legacy_report", groupId,
                null, parsed.Value.ReportedUserId, parsed.Value.Status,
                parsed.Value.CreatedAt ?? DateTimeOffset.UnixEpoch, $"redis:audit:{key}"));
            if (!parsed.IsSuccess) snapshot.Errors.Add(WithKey(key, null, parsed.Error));

            // Delivery payloads are transient Redis synchronization state and are intentionally
            // not copied into PostgreSQL. Old Telegram.Bot JSON shapes may no longer deserialize.
        }

        /// <summary>
        /// Captures the frozen <c>bot:general</c> counters as the PostgreSQL starting baseline.
        /// <para>
        /// Section 12.2 freezes this hash rather than reconciling it, so the values here no longer
        /// move. The write side is a one-shot insert: once a baseline exists PostgreSQL accumulates
        /// on top of it, and re-reading a frozen hash must not reset that accumulation.
        /// </para>
        /// </summary>
        private static async Task ReadBotBaselineAsync(string edition, OperationsBackfillSnapshot snapshot,
            DateTimeOffset capturedAt)
        {
            var fields = Hash(await Redis.db.HashGetAllAsync("bot:general"));
            if (fields.Count == 0) return;
            if (TryCounter(fields, "messages", out var messages) && TryCounter(fields, "users", out var users) &&
                TryCounter(fields, "ban", out var bans) && TryCounter(fields, "kick", out var kicks))
                snapshot.BotStatisticBaselines.Add(new BotStatisticBaseline(edition, messages, users, bans, kicks, capturedAt));
            else snapshot.Errors.Add(WithKey("bot:general", null, new FormatException("A bot statistic is not a non-negative integer.")));
        }

        private static bool TryCounter(IReadOnlyDictionary<string, string> fields, string name, out long value) =>
            !fields.TryGetValue(name, out var text) ? (value = 0) == 0 : TryNonNegativeInt64(text, out value);
        private static bool TryNonNegativeInt64(RedisValue value, out long result) =>
            long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;
        private static bool TryNonNegativeInt64(string value, out long result) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;
        private static Dictionary<string, string> Hash(IEnumerable<HashEntry> entries) =>
            entries.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString(), StringComparer.Ordinal);
        private static string Get(IReadOnlyDictionary<string, string> fields, string name) =>
            fields.TryGetValue(name, out var value) ? value : null;
        private static Exception WithKey(string key, object field, Exception error) =>
            new FormatException($"Redis record '{key}'{(field == null ? string.Empty : $" field '{field}'")} is malformed: {error.Message}", error);
    }
}
