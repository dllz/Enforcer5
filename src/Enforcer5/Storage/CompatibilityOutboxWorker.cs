using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Newtonsoft.Json;

namespace Enforcer5.Storage
{
    internal sealed class CompatibilityOutboxWorker : IDisposable
    {
        private readonly PostgresDatabase _database;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _worker;

        // Projects PostgreSQL writes back into Redis for rollback. Claims are durable rows, so a
        // pause leaves them claimed-but-incomplete and they are retried on resume.
        private static readonly Cutover.DurableWriter MirrorWriter =
            Cutover.QuiesceBarrier.Register("redis-rollback-mirror",
                Cutover.WriterResumeClass.Cursor);

        internal CompatibilityOutboxWorker(PostgresDatabase database)
        {
            _database = database;
        }

        internal void Start()
        {
            _worker ??= Task.Run(RunAsync);
        }

        internal void Stop(TimeSpan timeout)
        {
            _cts.Cancel();
            try { _worker?.Wait(timeout); }
            catch (Exception e) when (e is AggregateException || e is OperationCanceledException) { }
        }

        private async Task RunAsync()
        {
            await using var leaderConnection = await _database.OpenConnectionAsync(_cts.Token);
            const long leaderLockId = 0x454E464F55544258;
            var ownsLeaderLock = false;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!ownsLeaderLock)
                    {
                        ownsLeaderLock = await leaderConnection.ExecuteScalarAsync<bool>(new CommandDefinition(
                            "SELECT pg_try_advisory_lock(@LockId)", new { LockId = leaderLockId },
                            cancellationToken: _cts.Token));
                        if (!ownsLeaderLock)
                        {
                            await Task.Delay(1000, _cts.Token);
                            continue;
                        }
                    }
                    var entry = await ClaimAsync();
                    if (entry == null)
                    {
                        await Task.Delay(250, _cts.Token);
                        continue;
                    }

                    // The entry is claimed but not completed, so pausing before applying it leaves
                    // it pending and it is re-claimed on resume. Wrapping the apply keeps the
                    // barrier from declaring quiet part-way through projecting into Redis.
                    using (await MirrorWriter.BeginWorkAsync(_cts.Token))
                        await ApplyAsync(entry);
                    await CompleteAsync(entry.Id);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    LogHelper.Error($"Redis rollback-mirror worker failed: {e.Message}");
                    try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { }
                }
            }
        }

        private async Task<ClaimedOutboxEntry> ClaimAsync()
        {
            await using var connection = await _database.OpenConnectionAsync(_cts.Token);
            return await connection.QuerySingleOrDefaultAsync<ClaimedOutboxEntry>(new CommandDefinition(@"
UPDATE compatibility_outbox
SET attempt_count = attempt_count + 1,
    next_attempt_at = now() + interval '30 seconds'
WHERE id = (
    SELECT candidate.id FROM compatibility_outbox candidate
    WHERE candidate.completed_at IS NULL AND candidate.next_attempt_at <= now()
      AND NOT EXISTS (
          SELECT 1 FROM compatibility_outbox earlier
          WHERE earlier.completed_at IS NULL
            AND earlier.ordering_key = candidate.ordering_key
            AND (earlier.created_at, earlier.id) < (candidate.created_at, candidate.id))
    ORDER BY candidate.created_at, candidate.id
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
RETURNING id, domain, operation, payload::text AS payload",
                cancellationToken: _cts.Token));
        }

        private static async Task ApplyAsync(ClaimedOutboxEntry entry)
        {
            if (entry.Domain is "roles" or "elevation-blocks" or "policies" or "warnings" or "global-roles")
            {
                await ApplyModerationAsync(entry);
                return;
            }
            if (entry.Domain is "reports" or "report-deliveries" or "global-sanctions" or "statistics" or "sanctions")
            {
                await ApplyOperationsAsync(entry);
                return;
            }
            if (!string.Equals(entry.Domain, "group-settings", StringComparison.Ordinal))
            {
                await ApplyPrincipalContentAsync(entry);
                return;
            }

            var payload = JsonConvert.DeserializeObject<GroupSettingsMirrorPayload>(entry.Payload)
                          ?? throw new InvalidOperationException("Invalid group-settings mirror payload.");
            var key = $"chat:{payload.ChatId}:{payload.SettingGroup}";
            switch (entry.Operation)
            {
                case "set":
                    await Redis.db.HashSetAsync(key, payload.Field, payload.Value);
                    if (payload.SettingGroup == "settings" && payload.Field == "logchat")
                        await Redis.db.SetAddAsync("logChatGroups", payload.ChatId);
                    break;
                case "delete":
                    await Redis.db.HashDeleteAsync(key, payload.Field);
                    if (payload.SettingGroup == "settings" && payload.Field == "logchat")
                        await Redis.db.SetRemoveAsync("logChatGroups", payload.ChatId);
                    break;
                case "increment":
                    // PostgreSQL is authoritative. Project the resulting canonical value rather
                    // than replaying a delta that could be applied twice after a crash.
                    await Redis.db.HashSetAsync(key, payload.Field, payload.ResultValue);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported mirror operation '{entry.Operation}'.");
            }
        }

        private static async Task ApplyModerationAsync(ClaimedOutboxEntry entry)
        {
            dynamic payload = JsonConvert.DeserializeObject(entry.Payload);
            if (entry.Domain == "global-roles")
            {
                if ((bool)payload.active) await Redis.db.SetAddAsync("langAdmins", (long)payload.userId);
                else await Redis.db.SetRemoveAsync("langAdmins", (long)payload.userId);
                return;
            }
            if (entry.Domain == "warnings")
            {
                long warningGroupId = payload.groupId, userId = payload.userId;
                foreach (var balance in payload.balances)
                {
                    string type = balance.warningType;
                    var key = type == "standard" ? $"chat:{warningGroupId}:warns" :
                        type == "prewarning" ? $"chat:{warningGroupId}:prewarns" : $"chat:{warningGroupId}:mediawarn";
                    await Redis.db.HashSetAsync(key, userId, (long)balance.activeQuantity);
                    if (type == "standard") await Redis.db.HashSetAsync($"chat:{warningGroupId}:totalWarns",
                        userId, (long)balance.lifetimeQuantity);
                }
                return;
            }
            long gid = payload.groupId, uid = payload.userId;
            string redisKey;
            bool enabled;
            if (entry.Domain == "roles")
            {
                redisKey = (string)payload.role == "authorized" ? $"chat:{gid}:auth" : $"chat:{gid}:mod";
                enabled = payload.active;
            }
            else if (entry.Domain == "elevation-blocks")
            {
                redisKey = $"chat:{gid}:deauth"; enabled = payload.active;
            }
            else
            {
                string policy = payload.policy;
                redisKey = policy == "watch_disabled" ? $"chat:{gid}:watch" :
                    policy == "reports_blocked" ? $"chat:{gid}:reportblocked" :
                    policy == "admin_notifications_disabled" ? $"chat:{gid}:adminOff" : $"chat:{gid}:tagall2";
                enabled = payload.enabled;
            }
            if (enabled) await Redis.db.SetAddAsync(redisKey, uid); else await Redis.db.SetRemoveAsync(redisKey, uid);
        }

        private static async Task ApplyOperationsAsync(ClaimedOutboxEntry entry)
        {
            dynamic payload = JsonConvert.DeserializeObject(entry.Payload);
            if (entry.Domain == "global-sanctions")
            {
                long userId = payload.userId;
                await Redis.db.HashSetAsync($"globalBan:{userId}", new[]
                {
                    new StackExchange.Redis.HashEntry("banned", (bool)payload.active ? "1" : "0"),
                    new StackExchange.Redis.HashEntry("motivation", (string)payload.reason ?? ""),
                    new StackExchange.Redis.HashEntry("unbanMotivation", (string)payload.revocationReason ?? "")
                });
                return;
            }
            if (entry.Domain == "sanctions")
            {
                long sanctionGroupId = payload.groupId, userId = payload.userId;
                string kind = payload.kind;
                if (entry.Operation == "resolve")
                {
                    if (kind == "mute" || kind == "tempmute")
                    {
                        await Redis.db.SetRemoveAsync($"chat:{sanctionGroupId}:muted", userId);
                        await Redis.db.SetRemoveAsync($"chat:{sanctionGroupId}:mutedJoiners", userId);
                    }
                    if (kind == "ban" || kind == "tempban")
                    {
                        await Redis.db.SetRemoveAsync($"chat:{sanctionGroupId}:tempbanned", userId);
                        await Redis.db.SetRemoveAsync($"chat:{sanctionGroupId}:tempbannedPremium", userId);
                    }
                    return;
                }
                if (kind == "mute" || kind == "tempmute")
                    await Redis.db.SetAddAsync($"chat:{sanctionGroupId}:muted", userId);
                if (kind == "tempban" && payload.expiresAt != null)
                {
                    var expiry = DateTimeOffset.Parse((string)payload.expiresAt).ToUnixTimeSeconds();
                    string edition = payload.edition;
                    var indexKey = edition == "premium" ? "tempbannedPremium" : "tempbanned";
                    var groupKey = edition == "premium"
                        ? $"chat:{sanctionGroupId}:tempbannedPremium"
                        : $"chat:{sanctionGroupId}:tempbanned";
                    await Redis.db.HashSetAsync(indexKey, expiry, $"{sanctionGroupId}:{userId}::");
                    await Redis.db.SetAddAsync(groupKey, userId);
                    await Redis.db.StringSetAsync($"chat:{sanctionGroupId}:tempbanned:{userId}", expiry,
                        DateTimeOffset.FromUnixTimeSeconds(expiry) - DateTimeOffset.UtcNow);
                }
                return;
            }
            if (entry.Domain == "statistics")
            {
                long userId = payload.userId;
                await Redis.db.HashSetAsync("bot:general", "messages", (long)payload.botMessages);
                await Redis.db.HashSetAsync($"chat:{userId}", "msgs", (long)payload.userMessages);
                if (payload.groupId != null)
                {
                    long statsGroupId = payload.groupId;
                    await Redis.db.HashSetAsync($"{statsGroupId}:users:{userId}", "msgs", (long)payload.groupMessages);
                    await Redis.db.HashSetAsync($"chat:{statsGroupId}:userlast", userId,
                        DateTimeOffset.Parse((string)payload.occurredAt).LocalDateTime.Ticks);
                }
                return;
            }
            long groupId = payload.groupId, messageId = payload.reportMessageId;
            var key = $"flagged:{groupId}:{messageId}";
            if (entry.Domain == "report-deliveries")
            {
                if (entry.Operation == "count")
                    await Redis.db.HashSetAsync(key, "#Admin", (int)payload.count);
                else
                    await Redis.db.HashSetAsync(key, $"messageObject{(int)payload.index}", (string)payload.payload);
            }
            else if (entry.Operation == "solve")
            {
                await Redis.db.HashSetAsync(key, new[]
                {
                    new StackExchange.Redis.HashEntry("Solved", 1),
                    new StackExchange.Redis.HashEntry("SolvedAt", (string)payload.solvedAt),
                    new StackExchange.Redis.HashEntry("solvedBy", (string)payload.solverDisplay ?? ""),
                    new StackExchange.Redis.HashEntry("SolvedByUserId", (long)payload.solverUserId)
                });
            }
            else
            {
                await Redis.db.HashSetAsync(key, new[]
                {
                    new StackExchange.Redis.HashEntry("Solved", 0),
                    new StackExchange.Redis.HashEntry("Reporter", (string)payload.reporterDisplay ?? ""),
                    new StackExchange.Redis.HashEntry("ReporterUserId", (long)payload.reporterUserId),
                    new StackExchange.Redis.HashEntry("repID", messageId)
                });
            }
        }

        private static async Task ApplyPrincipalContentAsync(ClaimedOutboxEntry entry)
        {
            var payload = JsonConvert.DeserializeObject<dynamic>(entry.Payload);
            switch (entry.Domain)
            {
                case "users":
                    var previousUsername = (string)payload.previousUsername;
                    if (!string.IsNullOrEmpty(previousUsername) && previousUsername != (string)payload.username)
                        await Redis.db.HashDeleteAsync("bot:usernames", previousUsername);
                    await Redis.db.HashSetAsync($"user:{(long)payload.userId}", new[]
                    {
                        new StackExchange.Redis.HashEntry("name", (string)payload.name ?? string.Empty),
                        new StackExchange.Redis.HashEntry("username", (string)payload.username ?? string.Empty)
                    });
                    if (!string.IsNullOrEmpty((string)payload.username))
                        await Redis.db.HashSetAsync("bot:usernames", (string)payload.username, (long)payload.userId);
                    return;
                case "groups":
                    await Redis.db.SetAddAsync("bot:groupsid", (long)payload.chatId);
                    await Redis.db.HashSetAsync($"chat:{(long)payload.chatId}:details", "name",
                        (string)payload.name ?? string.Empty);
                    return;
                case "group-entitlements":
                    if ((string)payload.entitlement != "premium") return;
                    if ((bool)payload.enabled) await Redis.db.SetAddAsync("premiumBot", (long)payload.chatId);
                    else await Redis.db.SetRemoveAsync("premiumBot", (long)payload.chatId);
                    return;
                case "group-content":
                    var chatId = (long)payload.chatId;
                    var contentType = (string)payload.contentType;
                    if (contentType == "link")
                    {
                        if (entry.Operation == "delete") await Redis.db.HashDeleteAsync($"chat:{chatId}links", "link");
                        else await Redis.db.HashSetAsync($"chat:{chatId}links", "link", (string)payload.content);
                    }
                    else
                    {
                        var key = $"chat:{chatId}:{contentType}";
                        if (entry.Operation == "delete") await Redis.db.KeyDeleteAsync(key);
                        else await Redis.db.StringSetAsync(key, (string)payload.content);
                    }
                    return;
                case "custom-commands":
                    await ApplyCustomCommandAsync(entry, payload);
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported compatibility outbox domain '{entry.Domain}'.");
            }
        }

        private static async Task ApplyCustomCommandAsync(ClaimedOutboxEntry entry, dynamic payload)
        {
            var chatId = (long)payload.chatId;
            var trigger = (string)payload.trigger;
            if (entry.Operation != "delete-source" && entry.Operation != "restore-source" &&
                !trigger.StartsWith("#", StringComparison.Ordinal))
                trigger = $"#{trigger}";
            var key = $"chat:{chatId}:extra";
            var sideKey = $"{key}:{trigger}";
            if (entry.Operation == "delete" || entry.Operation == "delete-source")
            {
                await Redis.db.HashDeleteAsync(key, trigger);
                await Redis.db.KeyDeleteAsync(sideKey);
                return;
            }

            var content = (string)payload.content;
            var mediaType = (string)payload.mediaType;
            var fileId = (string)payload.fileId;
            if (!string.IsNullOrEmpty(fileId) && mediaType != "gif")
                content = $"###file_id!{mediaType}###:{fileId}";
            await Redis.db.HashSetAsync(key, trigger, content ?? string.Empty);
            if (mediaType == "gif" && !string.IsNullOrEmpty(fileId))
                await Redis.db.HashSetAsync(sideKey, "mediaid", fileId);
            else
                await Redis.db.HashDeleteAsync(sideKey, "mediaid");
            if (!string.IsNullOrEmpty((string)payload.caption))
                await Redis.db.HashSetAsync(sideKey, "caption", (string)payload.caption);
            else
                await Redis.db.HashDeleteAsync(sideKey, "caption");
        }

        private async Task CompleteAsync(Guid id)
        {
            await using var connection = await _database.OpenConnectionAsync(_cts.Token);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE compatibility_outbox SET completed_at = now(), last_error = NULL WHERE id = @Id",
                new { Id = id }, cancellationToken: _cts.Token));
        }

        public void Dispose()
        {
            Stop(TimeSpan.FromSeconds(15));
            _cts.Dispose();
        }

        private sealed class ClaimedOutboxEntry
        {
            public Guid Id { get; set; }
            public string Domain { get; set; }
            public string Operation { get; set; }
            public string Payload { get; set; }
        }

        private sealed class GroupSettingsMirrorPayload
        {
            public long ChatId { get; set; }
            public string SettingGroup { get; set; }
            public string Field { get; set; }
            public string Value { get; set; }
            public string ResultValue { get; set; }
        }
    }
}
