using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Models;
using Enforcer5.Storage;
using Newtonsoft.Json;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    internal interface ITelegramActionExecutor
    {
        Task ExecuteAsync(TelegramAction action, CancellationToken cancellationToken);
    }

    internal sealed class BotTelegramActionExecutor : ITelegramActionExecutor
    {
        public async Task ExecuteAsync(TelegramAction action, CancellationToken cancellationToken)
        {
            switch (action.Type)
            {
                case TelegramActionType.DeleteMessage:
                    await Bot.Api.DeleteMessage(action.ChatId, action.MessageId, cancellationToken: cancellationToken);
                    return;
                case TelegramActionType.BanUser:
                    await Bot.Api.BanChatMember(action.ChatId, action.UserId,
                        untilDate: action.UntilDateUnix == 0
                            ? null
                            : DateTimeOffset.FromUnixTimeSeconds(action.UntilDateUnix).UtcDateTime,
                        cancellationToken: cancellationToken);
                    return;
                case TelegramActionType.UnbanUser:
                    await Bot.Api.UnbanChatMember(action.ChatId, action.UserId,
                        cancellationToken: cancellationToken);
                    return;
                case TelegramActionType.RestrictUser:
                    await Bot.Api.RestrictChatMember(action.ChatId, action.UserId,
                        new ChatPermissions
                        {
                            CanSendMessages = action.CanSendMessages,
                            CanSendAudios = action.CanSendAudios,
                            CanSendDocuments = action.CanSendDocuments,
                            CanSendPhotos = action.CanSendPhotos,
                            CanSendVideos = action.CanSendVideos,
                            CanSendVideoNotes = action.CanSendVideoNotes,
                            CanSendVoiceNotes = action.CanSendVoiceNotes,
                            CanSendPolls = action.CanSendPolls,
                            CanSendOtherMessages = action.CanSendOtherMessages,
                            CanAddWebPagePreviews = action.CanAddWebPagePreviews
                        }, untilDate: action.UntilDateUnix == 0
                            ? null
                            : DateTimeOffset.FromUnixTimeSeconds(action.UntilDateUnix).UtcDateTime,
                        cancellationToken: cancellationToken);
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported Telegram action type: {action.Type}");
            }
        }
    }

    internal enum TelegramActionFailureKind
    {
        Success,
        Retry,
        Terminal
    }

    internal readonly struct TelegramActionFailure
    {
        internal TelegramActionFailure(TelegramActionFailureKind kind, TimeSpan retryAfter)
        {
            Kind = kind;
            RetryAfter = retryAfter;
        }

        internal TelegramActionFailureKind Kind { get; }
        internal TimeSpan RetryAfter { get; }
    }

    internal static class TelegramActionFailureClassifier
    {
        internal static TelegramActionFailure Classify(Exception exception, int attempt)
        {
            var api = Bot.AsApiError(exception);
            if (api != null)
            {
                var description = api.Message?.ToLowerInvariant() ?? string.Empty;
                if (description.Contains("message to delete not found"))
                    return new TelegramActionFailure(TelegramActionFailureKind.Success, TimeSpan.Zero);

                if (api.ErrorCode == 429)
                {
                    var seconds = Math.Max(1, api.Parameters?.RetryAfter ?? 5);
                    return new TelegramActionFailure(TelegramActionFailureKind.Retry, TimeSpan.FromSeconds(seconds));
                }

                if (api.ErrorCode >= 500)
                    return RetryWithBackoff(attempt);

                return new TelegramActionFailure(TelegramActionFailureKind.Terminal, TimeSpan.Zero);
            }

            if (exception is HttpRequestException || exception is TaskCanceledException || exception is TimeoutException)
                return RetryWithBackoff(attempt);

            return new TelegramActionFailure(TelegramActionFailureKind.Terminal, TimeSpan.Zero);
        }

        private static TelegramActionFailure RetryWithBackoff(int attempt)
        {
            var seconds = Math.Min(60, Math.Pow(2, Math.Min(attempt, 6)));
            return new TelegramActionFailure(TelegramActionFailureKind.Retry, TimeSpan.FromSeconds(seconds));
        }
    }

    internal sealed class TelegramActionQueue : IDisposable
    {
        private const string GroupName = "telegram-workers";

        // Bans, unbans and restrictions leave the process here. A quiesce must not cut across a
        // Telegram call that has been made but not yet acknowledged in the stream.
        private static readonly Storage.Cutover.DurableWriter ActionWriter =
            Storage.Cutover.QuiesceBarrier.Register("telegram-action-queue",
                Storage.Cutover.WriterResumeClass.Cursor);
        private const int ReadBatchSize = 20;
        // Must exceed the Telegram HTTP timeout; reclaiming an action while its original request is
        // still in flight can execute it twice. A crashed worker is recovered after this lease.
        private const long ReclaimAfterMs = 180_000;
        private static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(30);

        private const string RetryScript = @"
redis.call('HSET', KEYS[2], ARGV[1], ARGV[2])
redis.call('ZADD', KEYS[3], ARGV[3], ARGV[1])
redis.call('XACK', KEYS[1], ARGV[4], ARGV[5])
redis.call('XDEL', KEYS[1], ARGV[5])
return 1";

        private const string HandoffScript = @"
redis.call('XADD', KEYS[2], '*', 'payload', ARGV[1])
redis.call('XACK', KEYS[1], ARGV[2], ARGV[3])
redis.call('XDEL', KEYS[1], ARGV[3])
return 1";

        private const string MoveDueScript = @"
local ids = redis.call('ZRANGEBYSCORE', KEYS[2], '-inf', ARGV[1], 'LIMIT', 0, ARGV[2])
local moved = 0
for _, id in ipairs(ids) do
  local payload = redis.call('HGET', KEYS[3], id)
  if payload then
    redis.call('XADD', KEYS[1], '*', 'payload', payload)
    redis.call('HDEL', KEYS[3], id)
    moved = moved + 1
  end
  redis.call('ZREM', KEYS[2], id)
end
return moved";

        private const string CompleteScript = @"
redis.call('XACK', KEYS[1], ARGV[1], ARGV[2])
redis.call('XDEL', KEYS[1], ARGV[2])
return 1";

        private const string DeadLetterScript = @"
redis.call('XADD', KEYS[2], '*', 'actionId', ARGV[1], 'payload', ARGV[2], 'error', ARGV[3], 'failedAt', ARGV[4])
redis.call('XACK', KEYS[1], ARGV[5], ARGV[6])
redis.call('XDEL', KEYS[1], ARGV[6])
return 1";

        private readonly IDatabase _db;
        private readonly ITelegramActionExecutor _executor;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly string _streamKey;
        private readonly string _delayedKey;
        private readonly string _delayedPayloadKey;
        private readonly string _deadKey;
        private readonly string _fallbackKey;
        private readonly string _consumerPrefix;
        private readonly string _ownerEdition;
        private readonly string _executorEdition;
        private readonly string _requiredAbsentHeartbeat;
        private readonly int _workerCount;
        private Task[] _workers = Array.Empty<Task>();
        private Task _scheduler;
        private Task _reclaimer;
        private long _lastDeadLetterTrimHour = -1;

        internal TelegramActionQueue(IDatabase db, ITelegramActionExecutor executor, string ownerEdition,
            string executorEdition = null, bool fallbackStream = false,
            string requiredAbsentHeartbeat = null)
        {
            _db = db;
            _executor = executor;
            _ownerEdition = ownerEdition;
            _executorEdition = executorEdition ?? ownerEdition;
            _requiredAbsentHeartbeat = requiredAbsentHeartbeat;
            _streamKey = fallbackStream
                ? $"enforcer:{ownerEdition}:telegram:fallback"
                : $"enforcer:{ownerEdition}:telegram:moderation";
            var streamRole = fallbackStream ? "fallback" : "moderation";
            _delayedKey = $"enforcer:{ownerEdition}:telegram:{streamRole}:delayed";
            _delayedPayloadKey = $"enforcer:{ownerEdition}:telegram:{streamRole}:delayed-payload";
            _deadKey = $"enforcer:{ownerEdition}:telegram:dead";
            _fallbackKey = $"enforcer:{ownerEdition}:telegram:fallback";
            // Stable names prevent Redis consumer-group metadata growing on every deployment.
            // systemd enforces one process per edition on a host.
            _consumerPrefix = $"{Environment.MachineName}-{ownerEdition}-via-{_executorEdition}";
            _workerCount = Math.Clamp(RegHelper.GetInt("TelegramActionWorkers", 16), 1, 128);
        }

        internal async Task StartAsync()
        {
            try
            {
                await _db.StreamCreateConsumerGroupAsync(_streamKey, GroupName,
                    StreamPosition.Beginning, createStream: true);
            }
            catch (RedisServerException e) when (e.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
            {
            }

            await EnsureAutoClaimSupportedAsync();

            _workers = new Task[_workerCount];
            for (var i = 0; i < _workerCount; i++)
            {
                var consumer = $"{_consumerPrefix}-{i}";
                _workers[i] = Task.Run(() => WorkerLoopAsync(consumer));
            }

            _scheduler = Task.Run(SchedulerLoopAsync);
            _reclaimer = Task.Run(ReclaimerLoopAsync);
            LogHelper.Info($"Telegram action queue started with {_workerCount} workers ({_streamKey})");
        }

        private async Task EnsureAutoClaimSupportedAsync()
        {
            try
            {
                var result = await _db.ExecuteAsync("COMMAND", "INFO", "XAUTOCLAIM");
                if (result.IsNull || string.Equals(result.ToString(), "", StringComparison.Ordinal))
                    throw new InvalidOperationException();
            }
            catch (Exception e) when (e is RedisServerException || e is InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "The Telegram action queue requires Redis 6.2 or newer for XAUTOCLAIM crash recovery.", e);
            }
        }

        internal async Task EnqueueDeleteMessageAsync(long chatId, int messageId)
        {
            var action = TelegramAction.DeleteMessage(chatId, messageId, DateTimeOffset.UtcNow);
            action.DecisionOwner = _ownerEdition;
            action.PreferredExecutor = _ownerEdition;
            action.AllowedExecutors = new[] { _ownerEdition };
            await EnqueueAsync(action);
        }

        internal Task<RedisValue> EnqueueBanAsync(long chatId, long userId, DateTimeOffset? until = null) =>
            EnqueueAsync(TelegramAction.Ban(chatId, userId, DateTimeOffset.UtcNow, until));

        internal async Task<string> EnqueueUnbanTrackedAsync(long chatId, long userId)
        {
            var action = TelegramAction.Unban(chatId, userId, DateTimeOffset.UtcNow);
            action.CompletionKey = $"enforcer:telegram:completion:{action.Id}";
            await EnqueueAsync(action);
            return action.CompletionKey;
        }

        internal Task<string> EnqueueBanTrackedAsync(long chatId, long userId, DateTimeOffset? until = null) =>
            EnqueueTrackedAsync(TelegramAction.Ban(chatId, userId, DateTimeOffset.UtcNow, until));

        internal Task<string> EnqueueRestrictTrackedAsync(long chatId, long userId,
            ChatPermissions permissions, DateTimeOffset? until = null) =>
            EnqueueTrackedAsync(TelegramAction.Restrict(chatId, userId, DateTimeOffset.UtcNow, permissions, until));

        internal Task<string> EnqueueDeleteTrackedAsync(long chatId, int messageId) =>
            EnqueueTrackedAsync(TelegramAction.DeleteMessage(chatId, messageId, DateTimeOffset.UtcNow));

        private async Task<string> EnqueueTrackedAsync(TelegramAction action)
        {
            action.CompletionKey = $"enforcer:telegram:completion:{action.Id}";
            await EnqueueAsync(action);
            return action.CompletionKey;
        }

        internal Task<RedisValue> EnqueueAsync(TelegramAction action)
        {
            var payload = JsonConvert.SerializeObject(action);
            return _db.StreamAddAsync(_streamKey, new[] { new NameValueEntry("payload", payload) });
        }

        internal void Stop(TimeSpan timeout)
        {
            _cts.Cancel();
            var tasks = new List<Task>(_workers);
            if (_scheduler != null) tasks.Add(_scheduler);
            if (_reclaimer != null) tasks.Add(_reclaimer);
            try
            {
                Task.WhenAll(tasks).Wait(timeout);
            }
            catch (Exception e) when (e is AggregateException || e is OperationCanceledException)
            {
            }
        }

        private async Task WorkerLoopAsync(string consumer)
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // Safe point before claiming. Entries sit in a stream consumer group, so
                    // anything unacknowledged when the barrier rises is re-delivered on resume.
                    await ActionWriter.GateAsync(_cts.Token);

                    if (_requiredAbsentHeartbeat != null &&
                        await _db.KeyExistsAsync(MigrationMaintenanceGuard.HeartbeatKey(_requiredAbsentHeartbeat)))
                    {
                        await Task.Delay(1000, _cts.Token);
                        continue;
                    }
                    var entries = await _db.StreamReadGroupAsync(_streamKey, GroupName, consumer,
                        StreamPosition.NewMessages, 1);
                    if (entries.Length == 0)
                    {
                        await Task.Delay(25, _cts.Token);
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        // An action in flight calls Telegram and then acknowledges, so this scope
                        // is what stops the barrier declaring quiet mid-call.
                        using var work = await ActionWriter.BeginWorkAsync(_cts.Token);
                        await ProcessEntryAsync(entry);
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    LogHelper.Error($"Telegram action worker {consumer} failed: {e.Message}");
                    await DelayAfterFailureAsync();
                }
            }
        }

        private async Task ProcessEntryAsync(StreamEntry entry)
        {
            TelegramAction action;
            try
            {
                action = JsonConvert.DeserializeObject<TelegramAction>(entry["payload"]);
                if (action == null) throw new JsonSerializationException("Action payload was null");
            }
            catch (Exception e)
            {
                await DeadLetterAsync(entry, null, $"Invalid payload: {e.Message}");
                return;
            }

            if (action.DeadlineUnixMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > action.DeadlineUnixMs)
            {
                await RecordCompletionAsync(action, "failed", "Action deadline expired");
                await DeadLetterAsync(entry, action, "Action deadline expired");
                return;
            }

            if (!CanExecute(action))
            {
                await DelayAsync(entry.Id, action, TimeSpan.FromSeconds(1));
                return;
            }

            if (_executorEdition != action.PreferredExecutor)
            {
                if (!await TelegramCapabilityStore.CanModerateAsync(action.ChatId, _executorEdition))
                {
                    await DelayAsync(entry.Id, action, TimeSpan.FromSeconds(5));
                    return;
                }
            }

            try
            {
                await _executor.ExecuteAsync(action, _cts.Token);
                await RecordCompletionAsync(action, "succeeded", null);
                await CompleteAsync(entry.Id);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Leave it pending; XAUTOCLAIM will recover it after restart.
                throw;
            }
            catch (Exception e)
            {
                action.Attempt++;
                var failure = TelegramActionFailureClassifier.Classify(e, action.Attempt);
                if (failure.Kind == TelegramActionFailureKind.Success)
                {
                    await RecordCompletionAsync(action, "succeeded", null);
                    await CompleteAsync(entry.Id);
                }
                else if (failure.Kind == TelegramActionFailureKind.Retry)
                {
                    var api = Bot.AsApiError(e);
                    if (_ownerEdition == "premium" && _executorEdition == "premium" &&
                        action.Portable && action.AllowedExecutors?.Contains("normal") == true &&
                        (api?.ErrorCode == 429 || api?.ErrorCode >= 500 || e is HttpRequestException ||
                         e is TimeoutException || e is TaskCanceledException))
                    {
                        if (api?.ErrorCode == 429)
                            await TelegramCapabilityStore.SetCooldownAsync(_executorEdition, action.ChatId,
                                failure.RetryAfter);
                        await HandoffAsync(entry.Id, action);
                        return;
                    }
                    await DelayAsync(entry.Id, action, failure.RetryAfter);
                }
                else
                {
                    await RecordCompletionAsync(action, "failed", Bot.AsApiError(e)?.Message ?? e.Message);
                    await DeadLetterAsync(entry, action, Bot.AsApiError(e)?.Message ?? e.Message);
                }
            }
        }

        private Task RecordCompletionAsync(TelegramAction action, string status, string error)
        {
            if (string.IsNullOrEmpty(action?.CompletionKey)) return Task.CompletedTask;
            return RecordCompletionCoreAsync(action, status, error);
        }

        private async Task RecordCompletionCoreAsync(TelegramAction action, string status, string error)
        {
            await _db.HashSetAsync(action.CompletionKey, new HashEntry[]
            {
                new HashEntry("status", status),
                new HashEntry("error", error ?? string.Empty)
            });
            await _db.KeyExpireAsync(action.CompletionKey, TimeSpan.FromDays(1));
        }

        private Task<RedisResult> HandoffAsync(RedisValue streamId, TelegramAction action)
        {
            return _db.ScriptEvaluateAsync(HandoffScript,
                new RedisKey[] { _streamKey, _fallbackKey },
                new RedisValue[] { JsonConvert.SerializeObject(action), GroupName, streamId });
        }

        private bool CanExecute(TelegramAction action)
        {
            if (action.AllowedExecutors == null) return _executorEdition == action.DecisionOwner;
            foreach (var edition in action.AllowedExecutors)
                if (edition == _executorEdition) return action.Portable || edition == action.DecisionOwner;
            return false;
        }

        private async Task CompleteAsync(RedisValue streamId)
        {
            await _db.ScriptEvaluateAsync(CompleteScript,
                new RedisKey[] { _streamKey },
                new RedisValue[] { GroupName, streamId });
        }

        private Task<RedisResult> DelayAsync(RedisValue streamId, TelegramAction action, TimeSpan retryAfter)
        {
            var due = DateTimeOffset.UtcNow.Add(retryAfter).ToUnixTimeMilliseconds();
            return _db.ScriptEvaluateAsync(RetryScript,
                new RedisKey[] { _streamKey, _delayedPayloadKey, _delayedKey },
                new RedisValue[]
                {
                    action.Id,
                    JsonConvert.SerializeObject(action),
                    due,
                    GroupName,
                    streamId
                });
        }

        private async Task DeadLetterAsync(StreamEntry entry, TelegramAction action, string error)
        {
            await _db.ScriptEvaluateAsync(DeadLetterScript,
                new RedisKey[] { _streamKey, _deadKey },
                new RedisValue[]
                {
                    action?.Id ?? string.Empty,
                    entry["payload"],
                    error ?? string.Empty,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    GroupName,
                    entry.Id
                });
            LogHelper.Error($"Telegram action {action?.Id ?? entry.Id.ToString()} dead-lettered: {error}");
        }

        private async Task SchedulerLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await _db.ScriptEvaluateAsync(MoveDueScript,
                        new RedisKey[] { _streamKey, _delayedKey, _delayedPayloadKey },
                        new RedisValue[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 100 });
                    await TrimDeadLettersAsync();
                    await Task.Delay(25, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    LogHelper.Error($"Telegram delayed-action scheduler failed: {e.Message}");
                    await DelayAfterFailureAsync();
                }
            }
        }

        private async Task TrimDeadLettersAsync()
        {
            // Stream IDs are time-based, so MINID trims terminal actions without another index.
            // Run at most once per hour; the scheduler itself ticks much more frequently.
            var hour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600;
            if (Interlocked.Exchange(ref _lastDeadLetterTrimHour, hour) == hour) return;

            var cutoff = DateTimeOffset.UtcNow.Subtract(DeadLetterRetention).ToUnixTimeMilliseconds();
            await _db.ExecuteAsync("XTRIM", _deadKey, "MINID", "~", $"{cutoff}-0");
        }

        private async Task ReclaimerLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // XAUTOCLAIM needs Redis 6.2; production is 6.0.9. See RedisStreamReclaim.
                    var claimed = await RedisStreamReclaim.ClaimStaleAsync(_db, _streamKey, GroupName,
                        $"{_consumerPrefix}-reclaimer", ReclaimAfterMs, ReadBatchSize, _cts.Token);
                    foreach (var entry in claimed)
                        await ProcessEntryAsync(entry);
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    LogHelper.Error($"Telegram pending-action reclaimer failed: {e.Message}");
                    await DelayAfterFailureAsync();
                }
            }
        }

        private async Task DelayAfterFailureAsync()
        {
            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Dispose()
        {
            Stop(TimeSpan.FromSeconds(15));
            _cts.Dispose();
        }
    }
}
