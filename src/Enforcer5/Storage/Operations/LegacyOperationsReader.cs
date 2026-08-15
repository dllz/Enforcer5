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
                        // ban:{userId} is user-tier and had no migration path at all before this,
                        // so 121,061 keys of moderation history would have been left behind.
                        Redis.db.HashGetAllAsync($"ban:{userId}"));
                    return (UserId: userId, Global: values[0], Moderation: values[1]);
                }).ToArray();
                foreach (var read in await Task.WhenAll(reads))
                {
                    ReadGlobalSanction(read.UserId, read.Global, snapshot);
                    ReadModerationCounters(read.UserId, read.Moderation, snapshot);
                }
            }
            foreach (var groupId in groupIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadGroupAsync(groupId, snapshot, capturedAt);
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
            // No audit event: group_audit_events.group_id is NOT NULL and a global ban has no group,
            // so these 1,843 rows were built here only for the writer to filter them out again -
            // work done to produce a silent drop. The history they carried (reason, imposed_at,
            // resolution_reason, resolved_at) is held by the global_sanctions row itself.
            Record(snapshot, parsed.Status, key, parsed.Error);
        }

        /// <summary>
        /// Routes a non-success parse to the right list: an ambiguous record was still migrated and
        /// is a data-quality issue, a malformed one was not and is an error.
        /// </summary>
        private static void Record(OperationsBackfillSnapshot snapshot, LegacyParseStatus status,
            string key, Exception error)
        {
            // Built only once the status is known to be non-success: a successful parse carries no
            // error, and wrapping a null one throws.
            if (status == LegacyParseStatus.Success) return;
            var wrapped = WithKey(key, null, error);
            if (status == LegacyParseStatus.Ambiguous) snapshot.DataQualityIssues.Add(wrapped);
            else snapshot.Errors.Add(wrapped);
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

        private static async Task ReadGroupAsync(long groupId,
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

            // One HGETALL per group, not a keyspace walk. The message counts that used to be read
            // alongside this came from a full SCAN of *:users:* - 19.4M keys walked per invocation,
            // because MATCH filters after the walk - and are now loaded set-wise by Storage/Loading.
            // Only the last-seen timestamps are unique to this path.
            foreach (var entry in await Redis.db.HashGetAllAsync($"chat:{groupId}:userlast"))
            {
                if (!long.TryParse(entry.Name.ToString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var userId) || userId == 0)
                {
                    snapshot.Errors.Add(WithKey($"chat:{groupId}:userlast", entry.Name,
                        new FormatException("Last-seen user id is invalid.")));
                    continue;
                }
                var parsed = LegacyOperationParsers.ParseTicks(entry.Value.ToString());
                if (!parsed.IsSuccess)
                {
                    snapshot.Errors.Add(WithKey($"chat:{groupId}:userlast", entry.Name, parsed.Error));
                    continue;
                }
                snapshot.GroupUserLastSeen.Add(new GroupUserLastSeen(groupId, userId, parsed.Value));
            }
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
            Record(snapshot, parsed.Status, key, parsed.Error);

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
