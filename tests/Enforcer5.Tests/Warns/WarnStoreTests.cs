using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Warns
{
    /// <summary>
    /// The warn counts' queue in Redis while PostgreSQL is down (rollout step 12), against an in-memory store that can be
    /// made to fail or hang.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // WarnStore.Timeout and ErrorReporter.Observer are static
    public class WarnStoreTests
    {
        private const long ChatId = -1001234567890;

        private sealed class Flaky : IWarnRepository
        {
            public readonly MemoryWarns Inner = new();
            public bool Fail;
            public TaskCompletionSource<bool> Hang;

            private async Task Gate()
            {
                if (Hang != null) await Hang.Task;
                if (Fail) throw new InvalidOperationException("PostgreSQL is down");
            }

            public async Task<long> GetCountAsync(long chatId, WarnKind kind, long userId)
            {
                await Gate();
                return await Inner.GetCountAsync(chatId, kind, userId);
            }

            public async Task<WarnCounts> GetCountsAsync(long chatId, long userId)
            {
                await Gate();
                return await Inner.GetCountsAsync(chatId, userId);
            }

            public async Task<WarnChanged> ApplyAsync(WarnOp op)
            {
                await Gate();
                // Carried out once per id, as the PostgreSQL store does.
                if (Inner.Applied.Any(a => a.Id == op.Id))
                {
                    var now = Inner.Count(op.ChatId, op.Kind, op.UserId);
                    return new WarnChanged(now, now, now, null);
                }
                return await Inner.ApplyAsync(op);
            }

            public async Task SetLifetimeAsync(long chatId, long userId, long lifetime)
            {
                await Gate();
                await Inner.SetLifetimeAsync(chatId, userId, lifetime);
            }
        }

        private FakeRedis _redis;
        private Flaky _postgres;
        private bool _ready;
        private WarnStore _store;
        private List<string> _errors;
        private Action<string> _savedObserver;
        private TimeSpan _savedTimeout;

        [TestInitialize]
        public void SetUp()
        {
            _redis = new FakeRedis();
            _postgres = new Flaky();
            _ready = true;
            _store = new WarnStore(_postgres, () => _redis.Database, "normal", () => _ready);
            _errors = new List<string>();
            _savedObserver = ErrorReporter.Observer;
            ErrorReporter.Observer = e => { lock (_errors) _errors.Add(e); };
            _savedTimeout = WarnStore.Timeout;
        }

        [TestCleanup]
        public void TearDown()
        {
            ErrorReporter.Observer = _savedObserver;
            WarnStore.Timeout = _savedTimeout;
        }

        private static WarnOp Add(WarnKind kind = WarnKind.Warn, long user = 42) => new(ChatId, user, kind, WarnOperation.Add, 1);

        private Task<WarnChanged> ApplyAsync(WarnOp op) => _store.ApplyAsync(op);

        [TestMethod]
        public async Task WithPostgres_ChangesGoStraightThere_AndAreCached()
        {
            var first = await ApplyAsync(Add());
            var second = await ApplyAsync(Add());

            Assert.AreEqual((1L, 2L, false), (second.Before, second.After, second.Queued));
            Assert.IsNotNull(first.EventId);
            Assert.AreEqual(2L, _postgres.Inner.Count(ChatId, WarnKind.Warn, 42));
            Assert.AreEqual(0, _redis.Hash(_store.QueueKey).Count);
            Assert.AreEqual("2:2", _redis.Hash(WarnStore.CacheKey(ChatId))[WarnStore.CacheField(WarnKind.Warn, 42)].ToString());
            Assert.AreEqual(0, _errors.Count);
        }

        [TestMethod]
        public async Task PostgresNotReady_TheChangeIsQueued_WithAnEstimateFromTheCache()
        {
            await ApplyAsync(Add());
            _ready = false;

            var queued = await ApplyAsync(Add());

            Assert.AreEqual((1L, 2L, true, (long?)null), (queued.Before, queued.After, queued.Queued, queued.EventId));
            Assert.AreEqual(1, _redis.Hash(_store.QueueKey).Count);
            Assert.AreEqual(1, _store.Queued);
            Assert.AreEqual(2L, await _store.GetCountAsync(ChatId, WarnKind.Warn, 42), "reads give the estimate");
            Assert.AreEqual(1L, _postgres.Inner.Count(ChatId, WarnKind.Warn, 42), "not written yet");
        }

        [TestMethod]
        public async Task WithNothingCached_TheEstimateStartsFromTheOldHashes()
        {
            _ready = false;
            _redis.Hash("chat:-1001234567890:mediawarn")[42] = 2;

            var queued = await ApplyAsync(Add(WarnKind.Media));

            Assert.AreEqual((2L, 3L), (queued.Before, queued.After), "the media limit can still be reached");
        }

        [TestMethod]
        public async Task AFailingWrite_IsQueued_AndReported()
        {
            _postgres.Fail = true;

            var queued = await ApplyAsync(Add());

            Assert.IsTrue(queued.Queued);
            Assert.AreEqual(1, _redis.Hash(_store.QueueKey).Count);
            Assert.IsTrue(_errors.Single().Contains("queued in Redis"), _errors.Single());
        }

        [TestMethod]
        public async Task AWriteTakingTooLong_IsQueued_AndCountsOnceWhenBoth_GoThrough()
        {
            WarnStore.Timeout = TimeSpan.FromMilliseconds(50);
            _postgres.Hang = new TaskCompletionSource<bool>();

            var queued = await ApplyAsync(Add());
            Assert.IsTrue(queued.Queued);

            // The slow write finishes after all, then the queue is drained: one warn, not two.
            _postgres.Hang.SetResult(true);
            _postgres.Hang = null;
            await Task.Delay(50);
            await _store.DrainAsync();

            Assert.AreEqual(1L, _postgres.Inner.Count(ChatId, WarnKind.Warn, 42));
            Assert.AreEqual(0, _store.Queued);
            Assert.AreEqual(0, _redis.Hash(_store.QueueKey).Count);
        }

        [TestMethod]
        public async Task OncePostgresIsBack_TheQueueIsWrittenInOrder_AndNewChangesWaitBehindIt()
        {
            _ready = false;
            await ApplyAsync(Add());
            await ApplyAsync(Add());
            await ApplyAsync(new WarnOp(ChatId, 42, WarnKind.Warn, WarnOperation.Reset, 0));
            _ready = true;

            // Back, but the queue is not drained yet: this one queues behind the reset, or it would be wiped by it.
            var behind = await ApplyAsync(Add());
            Assert.IsTrue(behind.Queued);
            Assert.AreEqual(1L, behind.After);

            await _store.DrainAsync();

            CollectionAssert.AreEqual(new[] { WarnOperation.Add, WarnOperation.Add, WarnOperation.Reset, WarnOperation.Add },
                _postgres.Inner.Applied.Select(a => a.Operation).ToArray());
            Assert.AreEqual(1L, _postgres.Inner.Count(ChatId, WarnKind.Warn, 42));
            Assert.AreEqual(0, _store.Queued);
            Assert.IsFalse((await ApplyAsync(Add())).Queued, "straight to PostgreSQL again");
        }

        [TestMethod]
        public async Task ADrainThatFails_StopsThere_AndKeepsTheRest()
        {
            _ready = false;
            await ApplyAsync(Add());
            await ApplyAsync(Add());
            _ready = true;
            _postgres.Fail = true;

            await _store.DrainAsync();

            Assert.AreEqual(2, _store.Queued);
            Assert.AreEqual(2, _redis.Hash(_store.QueueKey).Count);
            Assert.AreEqual(1, _errors.Count);

            _postgres.Fail = false;
            await _store.DrainAsync();
            Assert.AreEqual(2L, _postgres.Inner.Count(ChatId, WarnKind.Warn, 42));
        }

        [TestMethod]
        public async Task AfterARestart_TheQueueIsPickedUp()
        {
            _ready = false;
            await ApplyAsync(Add());
            var restarted = new WarnStore(_postgres, () => _redis.Database, "normal", () => _ready);

            await restarted.LoadAsync();
            Assert.AreEqual(1, restarted.Queued);
            _ready = true;
            await restarted.DrainAsync();

            Assert.AreEqual(1L, _postgres.Inner.Count(ChatId, WarnKind.Warn, 42));
        }

        [TestMethod]
        public async Task EachEdition_HasItsOwnQueue()
        {
            var premium = new WarnStore(_postgres, () => _redis.Database, "premium", () => false);
            await premium.ApplyAsync(Add());
            Assert.AreEqual("warns:queue:premium", premium.QueueKey);
            Assert.AreEqual(1, _redis.Hash("warns:queue:premium").Count);
            Assert.AreEqual(0, _redis.Hash(_store.QueueKey).Count);
        }

        [TestMethod]
        public async Task TheLifetime_IsNotSetWhilePostgresIsAway()
        {
            // A data recovery an admin can apply again later.
            _ready = false;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _store.SetLifetimeAsync(ChatId, 42, 5));
        }
    }
}
