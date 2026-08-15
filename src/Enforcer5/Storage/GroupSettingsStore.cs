using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;

namespace Enforcer5.Storage
{
    internal static class GroupSettingsStore
    {
        private const string MutateScript = @"
if ARGV[4] == '1' and redis.call('XLEN', KEYS[3]) >= tonumber(ARGV[7]) then
  return redis.error_reply('group-settings PostgreSQL outbox capacity reached')
end
local result
if ARGV[1] == 'set' then
  result = redis.call('HSET', KEYS[1], ARGV[2], ARGV[3])
elseif ARGV[1] == 'delete' then
  result = redis.call('HDEL', KEYS[1], ARGV[2])
elseif ARGV[1] == 'increment' then
  result = redis.call('HINCRBY', KEYS[1], ARGV[2], ARGV[3])
else
  return redis.error_reply('unsupported group-settings operation')
end
local revision = redis.call('INCR', KEYS[2])
if ARGV[4] == '1' then
  redis.call('XADD', KEYS[3], '*', 'chatId', ARGV[5], 'settingGroup', ARGV[6])
end
local exists = redis.call('EXISTS', KEYS[1])
local fields = redis.call('HGETALL', KEYS[1])
return {result, revision, exists, fields}";

        private const string ObserveScript = @"
local revision = redis.call('INCR', KEYS[2])
local exists = redis.call('EXISTS', KEYS[1])
local fields = redis.call('HGETALL', KEYS[1])
return {revision, exists, fields}";

        private const string LogChatScript = @"
if ARGV[1] == 'set' then
  redis.call('HSET', KEYS[1], 'logchat', ARGV[2])
  redis.call('SADD', KEYS[4], ARGV[3])
else
  redis.call('HDEL', KEYS[1], 'logchat')
  redis.call('SREM', KEYS[4], ARGV[3])
end
redis.call('INCR', KEYS[2])
if ARGV[4] == '1' then
  redis.call('XADD', KEYS[3], '*', 'chatId', ARGV[3], 'settingGroup', 'settings')
end
return 1";

        private const string OutboxStream = "compat:group-settings:postgres-outbox";
        private const string OutboxGroup = "postgres-writers";
        private const int WriterCount = 2;
        private const long ReclaimAfterMs = 30_000;
        private static readonly object StartLock = new object();
        private static readonly Cutover.DurableWriter ShadowWriter =
            Cutover.QuiesceBarrier.Register("group-settings-shadow-writer",
                Cutover.WriterResumeClass.Cursor);
        private static readonly object MirrorLock = new object();
        private static readonly TimeSpan MirrorRebindTimeout = TimeSpan.FromSeconds(15);
        private static readonly CancellationTokenSource WriterCts = new CancellationTokenSource();
        private static Task _startTask;
        private static Task[] _writers = Array.Empty<Task>();
        private static PostgresDatabase _mirrorDatabase;
        private static CompatibilityOutboxWorker _mirrorWorker;
        private static string _mirrorConnection;
        private static int _mirrorGeneration;

        internal static async Task<bool> SetAsync(long chatId, string settingGroup, string field,
            RedisValue value)
        {
            if (StorageRouting.GroupPostgresIsPrimary(chatId))
                return await MutatePostgresAsync(chatId, settingGroup, "set", field, value.ToString()) != 0;
            var result = await MutateAsync(chatId, settingGroup, "set", field, value);
            return (long)result[0] != 0;
        }

        internal static async Task<bool> DeleteAsync(long chatId, string settingGroup, string field)
        {
            if (StorageRouting.GroupPostgresIsPrimary(chatId))
                return await MutatePostgresAsync(chatId, settingGroup, "delete", field, string.Empty) != 0;
            var result = await MutateAsync(chatId, settingGroup, "delete", field, RedisValue.EmptyString);
            return (long)result[0] != 0;
        }

        internal static async Task<long> IncrementAsync(long chatId, string settingGroup, string field,
            long delta = 1)
        {
            if (StorageRouting.GroupPostgresIsPrimary(chatId))
                return await MutatePostgresAsync(chatId, settingGroup, "increment", field, delta.ToString());
            var result = await MutateAsync(chatId, settingGroup, "increment", field, delta);
            return (long)result[0];
        }

        internal static Task<long> DecrementAsync(long chatId, string settingGroup, string field,
            long delta = 1) => IncrementAsync(chatId, settingGroup, field, -delta);

        internal static async Task SetLogChatAsync(long chatId, long? logChatId)
        {
            if (StorageRouting.GroupPostgresIsPrimary(chatId))
            {
                if (logChatId.HasValue)
                    await MutatePostgresAsync(chatId, GroupSettingGroups.Settings, "set", "logchat",
                        logChatId.Value.ToString());
                else
                    await MutatePostgresAsync(chatId, GroupSettingGroups.Settings, "delete", "logchat",
                        string.Empty);
                return;
            }
            if (ShadowEnabled) await EnsureWriterStartedAsync();
            await Redis.db.ScriptEvaluateAsync(LogChatScript,
                new RedisKey[]
                {
                    $"chat:{chatId}:settings",
                    $"compat:group-settings:{chatId}:settings:revision",
                    OutboxStream,
                    "logChatGroups"
                },
                new RedisValue[]
                {
                    logChatId.HasValue ? "set" : "delete",
                    logChatId?.ToString() ?? string.Empty,
                    chatId,
                    ShadowEnabled ? "1" : "0"
                });
        }

        /// <summary>
        /// The PostgreSQL connection the Redis rollback-mirror worker is currently bound to, or
        /// null when no worker is running.
        /// </summary>
        internal static string MirrorWorkerConnection
        {
            get { lock (MirrorLock) return _mirrorConnection; }
        }

        /// <summary>
        /// Number of rollback-mirror workers built since start-up. Increments only when a worker is
        /// actually constructed, so it distinguishes a genuine rebind from a no-op start.
        /// </summary>
        internal static int MirrorWorkerGeneration
        {
            get { lock (MirrorLock) return _mirrorGeneration; }
        }

        internal static Task StartAsync()
        {
            StartMirrorWorker();
            return ShadowEnabled ? EnsureWriterStartedAsync() : Task.CompletedTask;
        }

        /// <summary>
        /// Brings the Redis rollback-mirror worker in line with current configuration.
        /// <para>
        /// The worker used to be a one-shot static, so the first call bound it to whatever
        /// <c>PostgresConnection</c> happened to be set at the time and it stayed bound to that
        /// database for the life of the process - even after the connection or storage mode
        /// changed. It now tracks the connection it was built for and is rebuilt when that changes.
        /// </para>
        /// </summary>
        private static void StartMirrorWorker()
        {
            // Resolve the connection once, so the mode decision and the pool the worker gets are
            // based on the same value rather than two separate live config reads.
            var connection = StorageRouting.RedisMirrorEnabled
                ? RegHelper.GetRegValue("PostgresConnection")
                : null;

            lock (MirrorLock)
            {
                if (string.IsNullOrWhiteSpace(connection))
                {
                    StopMirrorWorkerLocked(MirrorRebindTimeout);
                    return;
                }

                if (_mirrorWorker != null &&
                    string.Equals(_mirrorConnection, connection, StringComparison.Ordinal))
                {
                    _mirrorWorker.Start();
                    return;
                }

                // A worker's cancellation source is single-use, so rebinding needs a new worker
                // rather than a restart of the existing one.
                StopMirrorWorkerLocked(MirrorRebindTimeout);
                _mirrorDatabase = PostgresDatabase.Shared(connection);
                _mirrorWorker = new CompatibilityOutboxWorker(_mirrorDatabase);
                _mirrorConnection = connection;
                _mirrorGeneration++;
                _mirrorWorker.Start();
            }
        }

        private static void StopMirrorWorkerLocked(TimeSpan timeout)
        {
            if (_mirrorWorker == null) return;
            try
            {
                _mirrorWorker.Stop(timeout);
                _mirrorWorker.Dispose();
            }
            catch (Exception e)
            {
                LogHelper.Error($"Stopping the Redis rollback-mirror worker failed: {e.Message}");
            }
            finally
            {
                _mirrorWorker = null;
                // The database is a shared instance owned by the process, so it is not disposed here.
                _mirrorDatabase = null;
                _mirrorConnection = null;
            }
        }

        internal static async Task<GroupSettingsSnapshot> ObserveAsync(long chatId, string settingGroup)
        {
            ValidateGroup(settingGroup);
            var redisKey = $"chat:{chatId}:{settingGroup}";
            var revisionKey = $"compat:group-settings:{chatId}:{settingGroup}:revision";
            var raw = (RedisResult[])await Redis.db.ScriptEvaluateAsync(ObserveScript,
                new RedisKey[] { redisKey, revisionKey });
            return BuildSnapshot(chatId, settingGroup, redisKey, (long)raw[0], (long)raw[1] != 0,
                (RedisResult[])raw[2]);
        }

        internal static async Task<HashEntry[]> GetAllAsync(long chatId, string settingGroup)
        {
            ValidateGroup(settingGroup);
            if (!StorageRouting.GroupPostgresIsPrimary(chatId))
                return await Redis.db.HashGetAllAsync($"chat:{chatId}:{settingGroup}");

            await using var database = PostgresDatabase.FromConfiguration();
            var fields = await new GroupSettingsRepository(database).GetAsync(chatId, settingGroup);
            return fields.Select(pair => new HashEntry(pair.Key, pair.Value)).ToArray();
        }

        internal static async Task<RedisValue> GetAsync(long chatId, string settingGroup, string field)
        {
            var fields = await GetAllAsync(chatId, settingGroup);
            foreach (var entry in fields)
                if (entry.Name == field) return entry.Value;
            return RedisValue.Null;
        }

        internal static async Task ObserveAndShadowAsync(long chatId, string settingGroup)
        {
            var snapshot = await ObserveAsync(chatId, settingGroup);
            if (!ShadowEnabled) return;
            await EnsureWriterStartedAsync();
            await Redis.db.StreamAddAsync(OutboxStream, new[]
            {
                new NameValueEntry("chatId", chatId),
                new NameValueEntry("settingGroup", settingGroup)
            });
        }

        private static async Task<RedisResult[]> MutateAsync(long chatId, string settingGroup,
            string operation, string field, RedisValue value)
        {
            ValidateGroup(settingGroup);
            if (ShadowEnabled) await EnsureWriterStartedAsync();
            var redisKey = $"chat:{chatId}:{settingGroup}";
            var revisionKey = $"compat:group-settings:{chatId}:{settingGroup}:revision";
            var raw = (RedisResult[])await Redis.db.ScriptEvaluateAsync(MutateScript,
                new RedisKey[] { redisKey, revisionKey, OutboxStream },
                new RedisValue[]
                {
                    operation, field, value, ShadowEnabled ? "1" : "0", chatId, settingGroup,
                    OutboxCapacity
                });
            return raw;
        }

        private static async Task<long> MutatePostgresAsync(long chatId, string settingGroup,
            string operation, string field, string value)
        {
            ValidateGroup(settingGroup);
            await using var database = PostgresDatabase.FromConfiguration();
            return await new GroupSettingsRepository(database).MutateCanonicalAsync(chatId, settingGroup,
                operation, field, value, StorageRouting.GroupRedisMirrorEnabled(chatId));
        }

        private static GroupSettingsSnapshot BuildSnapshot(long chatId, string settingGroup,
            string redisKey, long revision, bool exists, RedisResult[] values)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < values.Length; i += 2)
                fields[values[i].ToString()] = values[i + 1].ToString();
            return new GroupSettingsSnapshot(chatId, settingGroup, redisKey, fields, exists,
                revision, DateTimeOffset.UtcNow);
        }

        internal static void Stop(TimeSpan timeout)
        {
            WriterCts.Cancel();
            lock (MirrorLock)
            {
                StopMirrorWorkerLocked(timeout);
            }
            try
            {
                Task.WhenAll(_writers).Wait(timeout);
            }
            catch (Exception e) when (e is AggregateException || e is OperationCanceledException)
            {
            }
        }

        private static bool ShadowEnabled =>
            string.Equals(RegHelper.GetRegValue("GroupSettingsShadowEnabled"), "true",
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection"));

        private static int OutboxCapacity =>
            Math.Clamp(RegHelper.GetInt("GroupSettingsOutboxCapacity", 100_000), 1_000, 1_000_000);

        private static Task EnsureWriterStartedAsync()
        {
            lock (StartLock)
            {
                return _startTask ??= StartWritersAsync();
            }
        }

        private static async Task StartWritersAsync()
        {
            try
            {
                await Redis.db.StreamCreateConsumerGroupAsync(OutboxStream, OutboxGroup,
                    StreamPosition.Beginning, createStream: true);
            }
            catch (RedisServerException e) when (e.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
            {
            }

            _writers = new Task[WriterCount];
            for (var i = 0; i < WriterCount; i++)
            {
                var consumer = $"{Environment.MachineName}-{EditionName}-{i}";
                _writers[i] = Task.Run(() => ShadowWriterAsync(consumer));
            }
        }

        private static async Task ShadowWriterAsync(string consumer)
        {
            await using var database = PostgresDatabase.FromConfiguration();
            var repository = new GroupSettingsRepository(database);
            var lastReclaim = DateTime.UtcNow;
            var reclaimCursor = (RedisValue)"0-0";
            while (!WriterCts.IsCancellationRequested)
            {
                try
                {
                    // Safe point. The stream consumer group holds the durable position, so pausing
                    // here leaves unacknowledged entries pending and they are re-delivered on
                    // resume. Gate before reading so a quiesce is not held up by a full batch.
                    await ShadowWriter.GateAsync(WriterCts.Token);

                    StreamEntry[] entries;
                    if (DateTime.UtcNow - lastReclaim >= TimeSpan.FromSeconds(30))
                    {
                        // XAUTOCLAIM needs Redis 6.2; production is 6.0.9. See RedisStreamReclaim.
                        entries = await RedisStreamReclaim.ClaimStaleAsync(Redis.db, OutboxStream,
                            OutboxGroup, consumer, ReclaimAfterMs, 20, WriterCts.Token);
                        lastReclaim = DateTime.UtcNow;
                        // Nothing stale this pass, so fall through to new entries rather than
                        // idling for the next 30 seconds.
                        if (entries.Length == 0)
                            entries = await Redis.db.StreamReadGroupAsync(OutboxStream, OutboxGroup,
                                consumer, StreamPosition.NewMessages, 20);
                    }
                    else
                    {
                        entries = await Redis.db.StreamReadGroupAsync(OutboxStream, OutboxGroup,
                            consumer, StreamPosition.NewMessages, 20);
                    }

                    if (entries.Length == 0)
                    {
                        await Task.Delay(100, WriterCts.Token);
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        using var work = await ShadowWriter.BeginWorkAsync(WriterCts.Token);
                        if (!long.TryParse(entry["chatId"].ToString(), out var chatId) ||
                            !GroupSettingGroups.IsSupported(entry["settingGroup"].ToString()))
                        {
                            await CompleteOutboxEntryAsync(entry.Id);
                            LogHelper.Error($"Invalid group-settings outbox entry {entry.Id}");
                            continue;
                        }

                        var settingGroup = entry["settingGroup"].ToString();
                        var snapshot = await ObserveAsync(chatId, settingGroup);
                        await repository.UpsertAsync(snapshot);
                        await CompleteOutboxEntryAsync(entry.Id);
                    }
                }
                catch (OperationCanceledException) when (WriterCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    LogHelper.Error($"Group-settings PostgreSQL writer {consumer} failed: {e.Message}");
                    try { await Task.Delay(1000, WriterCts.Token); } catch (OperationCanceledException) { }
                }
            }
        }

        private static Task<RedisResult> CompleteOutboxEntryAsync(RedisValue entryId)
        {
            return Redis.db.ScriptEvaluateAsync(@"
redis.call('XACK', KEYS[1], ARGV[1], ARGV[2])
redis.call('XDEL', KEYS[1], ARGV[2])
return 1", new RedisKey[] { OutboxStream }, new RedisValue[] { OutboxGroup, entryId });
        }

#if PREMIUM
        private const string EditionName = "premium";
#else
        private const string EditionName = "normal";
#endif

        private static void ValidateGroup(string settingGroup)
        {
            if (!GroupSettingGroups.IsSupported(settingGroup))
                throw new ArgumentOutOfRangeException(nameof(settingGroup), settingGroup,
                    "Unsupported group settings hash");
        }
    }
}
