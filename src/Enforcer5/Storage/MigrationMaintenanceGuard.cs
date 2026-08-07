using System;
using System.Threading.Tasks;
using System.Threading;
using Enforcer5.Helpers;

namespace Enforcer5.Storage
{
    internal static class MigrationMaintenanceGuard
    {
        internal static string HeartbeatKey(string edition) => $"enforcer:runtime:{edition}:heartbeat";
        private const string LockKey = "enforcer:migration:maintenance-lock";

        internal static async Task<bool> PublishHeartbeatAsync(string edition)
        {
            const string script = @"
if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
redis.call('SET', KEYS[2], ARGV[1], 'PX', 15000)
return 1";
            return (long)await Redis.db.ScriptEvaluateAsync(script,
                new StackExchange.Redis.RedisKey[] { LockKey, HeartbeatKey(edition) },
                new StackExchange.Redis.RedisValue[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) == 1;
        }

        internal static async Task RequireBotsStoppedAsync()
        {
            var active = await Redis.db.KeyExistsAsync(new StackExchange.Redis.RedisKey[]
            {
                HeartbeatKey("normal"), HeartbeatKey("premium")
            });
            if (active > 0)
                throw new InvalidOperationException(
                    "Destructive reconciliation requires both Enforcer services stopped. " +
                    "Wait 15 seconds for runtime heartbeats to expire and retry.");
        }

        internal static async Task<MaintenanceLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            var owner = Guid.NewGuid().ToString("N");
            if (!await Redis.db.StringSetAsync(LockKey, owner, TimeSpan.FromSeconds(30),
                    StackExchange.Redis.When.NotExists))
                throw new InvalidOperationException("Another migration maintenance operation is active.");
            try
            {
                await RequireBotsStoppedAsync();
                return new MaintenanceLease(owner, cancellationToken);
            }
            catch
            {
                await ReleaseAsync(owner);
                throw;
            }
        }

        internal static async Task EnsureRuntimeAllowedAsync()
        {
            if (await Redis.db.KeyExistsAsync(LockKey))
                throw new InvalidOperationException("Migration maintenance lock is active; bot startup refused.");
        }

        internal sealed class MaintenanceLease : IAsyncDisposable
        {
            private readonly string _owner;
            private readonly CancellationTokenSource _cts;
            private readonly Task _renewal;
            internal CancellationToken Token => _cts.Token;

            internal MaintenanceLease(string owner, CancellationToken cancellationToken)
            {
                _owner = owner;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _renewal = Task.Run(RenewAsync);
            }

            private async Task RenewAsync()
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
                        var renewed = (long)await Redis.db.ScriptEvaluateAsync(@"
if redis.call('GET', KEYS[1]) == ARGV[1] then
  redis.call('PEXPIRE', KEYS[1], 30000)
  return 1
end
return 0", new StackExchange.Redis.RedisKey[] { LockKey },
                            new StackExchange.Redis.RedisValue[] { _owner });
                        if (renewed != 1)
                            throw new InvalidOperationException("Migration maintenance lease was lost.");
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                }
                catch
                {
                    _cts.Cancel();
                    throw;
                }
            }

            public async ValueTask DisposeAsync()
            {
                _cts.Cancel();
                try { await _renewal; } catch (OperationCanceledException) { }
                await ReleaseAsync(_owner);
                _cts.Dispose();
            }
        }

        private static Task<StackExchange.Redis.RedisResult> ReleaseAsync(string owner) =>
            Redis.db.ScriptEvaluateAsync(@"
if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
return 0", new StackExchange.Redis.RedisKey[] { LockKey },
                new StackExchange.Redis.RedisValue[] { owner });
    }
}
