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
            CancellationToken cancellationToken = default)
        {
            var snapshot = new OperationsBackfillSnapshot();
            var capturedAt = DateTimeOffset.UtcNow;
            await ReadTempBansAsync("tempbanned", Editions.Normal, snapshot, capturedAt, cancellationToken);
            await ReadTempBansAsync("tempbannedPremium", Editions.Premium, snapshot, capturedAt, cancellationToken);
            await ReadBotBaselineAsync(Editions.Normal, snapshot, capturedAt);

            foreach (var userId in knownUserIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadGlobalSanctionAsync(userId, snapshot);
                await ReadUserStatisticsAsync(userId, snapshot, capturedAt);
            }
            foreach (var groupId in knownGroupIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadGroupAsync(groupId, knownUserIds, snapshot, capturedAt);
                if (reportMessageIds != null && reportMessageIds.TryGetValue(groupId, out var ids))
                    foreach (var messageId in ids.Distinct()) await ReadReportAsync(groupId, messageId, snapshot);
            }
            return snapshot;
        }

        private static async Task ReadTempBansAsync(string hash, string edition,
            OperationsBackfillSnapshot snapshot, DateTimeOffset capturedAt, CancellationToken token)
        {
            foreach (var entry in await Redis.db.HashGetAllAsync(hash))
            {
                token.ThrowIfCancellationRequested();
                var parsed = LegacyOperationParsers.ParseTempBan(entry.Name.ToString(), entry.Value.ToString(), edition);
                if (!parsed.IsSuccess) { snapshot.Errors.Add(WithKey(hash, entry.Name, parsed.Error)); continue; }
                var value = parsed.Value;
                snapshot.Sanctions.Add(new Sanction(0, value.GroupId, value.UserId,
                    SanctionKinds.TempBan, SanctionStatuses.Active,
                    edition, null, null, capturedAt, value.ExpiresAt, null, null, null,
                    $"redis:{hash}:{entry.Name}"));
                snapshot.AuditEvents.Add(new AuditEvent(0, edition, "legacy_tempban", value.GroupId,
                    null, value.UserId, entry.Value.ToString(), capturedAt,
                    $"redis:audit:{hash}:{entry.Name}"));
            }
        }

        private static async Task ReadGlobalSanctionAsync(long userId, OperationsBackfillSnapshot snapshot)
        {
            var key = $"globalBan:{userId}";
            var entries = await Redis.db.HashGetAllAsync(key);
            if (entries.Length == 0) return;
            var parsed = LegacyOperationParsers.ParseGlobalBan(userId, Hash(entries), $"redis:{key}");
            if (parsed.Value != null) snapshot.GlobalSanctions.Add(parsed.Value);
            if (parsed.Value != null)
                snapshot.AuditEvents.Add(new AuditEvent(0, Editions.Shared, "legacy_global_sanction",
                    null, null, userId, parsed.Value.Reason, parsed.Value.CreatedAt ?? DateTimeOffset.UnixEpoch,
                    $"redis:audit:{key}"));
            if (!parsed.IsSuccess) snapshot.Errors.Add(WithKey(key, null, parsed.Error));
        }

        private static async Task ReadUserStatisticsAsync(long userId, OperationsBackfillSnapshot snapshot,
            DateTimeOffset capturedAt)
        {
            var value = await Redis.db.HashGetAsync($"chat:{userId}", "msgs");
            if (!value.HasValue) return;
            if (TryNonNegativeInt64(value, out var messages))
                snapshot.UserStatistics.Add(new UserStatistic(userId, messages, capturedAt));
            else snapshot.Errors.Add(WithKey($"chat:{userId}", "msgs", new FormatException("Message count is not a non-negative integer.")));
        }

        private static async Task ReadGroupAsync(long groupId, IEnumerable<long> knownUserIds,
            OperationsBackfillSnapshot snapshot, DateTimeOffset capturedAt)
        {
            var banned = await Redis.db.SetMembersAsync($"chat:{groupId}:bannedlist");
            foreach (var member in banned)
            {
                if (!long.TryParse(member.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
                {
                    snapshot.Errors.Add(WithKey($"chat:{groupId}:bannedlist", member, new FormatException("Banned user id is invalid.")));
                    continue;
                }
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
            foreach (var userId in knownUserIds.Distinct())
            {
                var count = await Redis.db.HashGetAsync($"{groupId}:users:{userId}", "msgs");
                if (!count.HasValue && !last.ContainsKey(userId.ToString(CultureInfo.InvariantCulture))) continue;
                if (!TryNonNegativeInt64(count, out var messages))
                {
                    snapshot.Errors.Add(WithKey($"{groupId}:users:{userId}", "msgs", new FormatException("Message count is not a non-negative integer.")));
                    continue;
                }
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

        private static async Task ReadReportAsync(long groupId, long messageId,
            OperationsBackfillSnapshot snapshot)
        {
            var key = $"flagged:{groupId}:{messageId}";
            var fields = Hash(await Redis.db.HashGetAllAsync(key));
            if (fields.Count == 0) return;
            var parsed = LegacyOperationParsers.ParseReport(groupId, messageId, fields, $"redis:{key}");
            if (parsed.Value == null) { snapshot.Errors.Add(WithKey(key, null, parsed.Error)); return; }
            var temporaryId = -(snapshot.Reports.Count + 1L);
            snapshot.Reports.Add(parsed.Value with { Id = temporaryId });
            snapshot.AuditEvents.Add(new AuditEvent(0, Editions.Shared, "legacy_report", groupId,
                null, parsed.Value.ReportedUserId, parsed.Value.Status,
                parsed.Value.CreatedAt ?? DateTimeOffset.UnixEpoch, $"redis:audit:{key}"));
            if (!parsed.IsSuccess) snapshot.Errors.Add(WithKey(key, null, parsed.Error));

            if (fields.TryGetValue("#Admin", out var countText) &&
                int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 0)
            {
                for (var i = 0; i < count; i++)
                {
                    if (!fields.TryGetValue($"messageObject{i}", out var json))
                    {
                        snapshot.Errors.Add(WithKey(key, $"messageObject{i}", new FormatException("Report delivery is missing.")));
                        continue;
                    }
                    var delivery = LegacyOperationParsers.ParseReportDelivery(temporaryId, i, json);
                    if (delivery.IsSuccess) snapshot.ReportDeliveries.Add(delivery.Value);
                    else snapshot.Errors.Add(WithKey(key, $"messageObject{i}", delivery.Error));
                }
            }
            else snapshot.Errors.Add(WithKey(key, "#Admin", new FormatException("Report delivery count is invalid.")));
        }

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
