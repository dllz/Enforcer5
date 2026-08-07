using System.Collections.Concurrent;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Xunit;

namespace Enforcer5.Tests;

public sealed class TelegramActionQueueIntegrationTests
{
    [RedisFact]
    public async Task SuccessfulActionIsAcknowledgedAndRemoved()
    {
        await using var fixture = await QueueFixture.CreateAsync(new RecordingExecutor());

        await fixture.Queue.EnqueueDeleteMessageAsync(-100123, 456);

        await fixture.Executor.WaitForCallsAsync(1);
        await WaitUntilAsync(async () => await fixture.Database.StreamLengthAsync(fixture.StreamKey) == 0);

        Assert.Single(fixture.Executor.Actions);
    }

    [RedisFact]
    public async Task RateLimitedActionIsDelayedThenRetried()
    {
        var executor = new RecordingExecutor(attempt => attempt == 1
            ? new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 1 })
            : null);
        await using var fixture = await QueueFixture.CreateAsync(executor);

        await fixture.Queue.EnqueueDeleteMessageAsync(-100123, 456);

        await executor.WaitForCallsAsync(2, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(async () => await fixture.Database.StreamLengthAsync(fixture.StreamKey) == 0);

        Assert.Equal(2, executor.Actions.Count);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("Condition was not met before the timeout.");
    }

    private sealed class RecordingExecutor : ITelegramActionExecutor
    {
        private readonly Func<int, Exception> _failure;
        private readonly TaskCompletionSource _callsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        internal RecordingExecutor(Func<int, Exception> failure = null)
        {
            _failure = failure;
        }

        internal ConcurrentQueue<TelegramAction> Actions { get; } = new();

        public Task ExecuteAsync(TelegramAction action, CancellationToken cancellationToken)
        {
            Actions.Enqueue(action);
            var attempt = Interlocked.Increment(ref _attempts);
            _callsChanged.TrySetResult();
            var failure = _failure?.Invoke(attempt);
            return failure == null ? Task.CompletedTask : Task.FromException(failure);
        }

        internal async Task WaitForCallsAsync(int expected, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(3));
            while (Volatile.Read(ref _attempts) < expected && DateTime.UtcNow < deadline)
            {
                await Task.WhenAny(_callsChanged.Task, Task.Delay(25));
            }
            Assert.True(Volatile.Read(ref _attempts) >= expected,
                $"Expected {expected} calls but observed {_attempts}.");
        }
    }

    private sealed class QueueFixture : IAsyncDisposable
    {
        private readonly ConnectionMultiplexer _connection;
        private readonly string[] _keys;

        private QueueFixture(ConnectionMultiplexer connection, IDatabase database, TelegramActionQueue queue,
            RecordingExecutor executor, string edition)
        {
            _connection = connection;
            Database = database;
            Queue = queue;
            Executor = executor;
            StreamKey = $"enforcer:{edition}:telegram:moderation";
            _keys =
            [
                StreamKey,
                $"enforcer:{edition}:telegram:delayed",
                $"enforcer:{edition}:telegram:delayed-payload",
                $"enforcer:{edition}:telegram:dead"
            ];
        }

        internal IDatabase Database { get; }
        internal TelegramActionQueue Queue { get; }
        internal RecordingExecutor Executor { get; }
        internal string StreamKey { get; }

        internal static async Task<QueueFixture> CreateAsync(RecordingExecutor executor)
        {
            var connection = await ConnectionMultiplexer.ConnectAsync(
                Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!);
            var database = connection.GetDatabase();
            var edition = $"test-{Guid.NewGuid():N}";
            var queue = new TelegramActionQueue(database, executor, edition);
            var fixture = new QueueFixture(connection, database, queue, executor, edition);
            await database.KeyDeleteAsync(Array.ConvertAll(fixture._keys, key => (RedisKey)key));
            await queue.StartAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Queue.Dispose();
            await Database.KeyDeleteAsync(Array.ConvertAll(_keys, key => (RedisKey)key));
            await _connection.CloseAsync();
            _connection.Dispose();
        }
    }
}
