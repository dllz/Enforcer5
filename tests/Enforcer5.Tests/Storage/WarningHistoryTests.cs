using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Models;
using Enforcer5.Storage;
using Enforcer5.Tests.Warns;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Storage
{
    /// <summary>
    /// The warn counts and their history in PostgreSQL (rollout steps 4 and 12), against a real database; skipped
    /// without the test database.
    /// </summary>
    [TestClass]
    public class PostgresWarnRepositoryTests
    {
        private string _connectionString;
        private PostgresDatabase _database;
        private FakeRedis _redis;
        private PostgresWarnRepository _repo;
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        [TestInitialize]
        public async Task Setup()
        {
            _connectionString = await TestPostgres.MigratedSchemaAsync();
            _database = new PostgresDatabase(_connectionString);
            _redis = new FakeRedis();
            _repo = new PostgresWarnRepository(() => _database, new LegacyWarnCounts(() => _redis.Database));
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await TestPostgres.ReleaseMigratedAsync(_connectionString);
        }

        private Task<WarnChanged> Apply(WarnKind kind, WarnOperation op, long quantity = 1, long chat = -1001, long user = 5,
            long? actor = 9, string reason = null, int minutes = 0, string id = null) =>
            _repo.ApplyAsync(new WarnOp(chat, user, kind, op, quantity)
            {
                ActorId = actor, Reason = reason, At = T0.AddMinutes(minutes), Id = id ?? Guid.NewGuid().ToString("N")
            });

        private async Task<WarnBalance> Balance(string type, long chat = -1001) =>
            (await _repo.GetBalancesAsync(chat, 5)).SingleOrDefault(b => b.WarningType == type);

        [TestMethod]
        public async Task Counts_AddRemoveAndReset_AndTheLifetimeCountsAdds()
        {
            Assert.AreEqual((0L, 1L), ((await Apply(WarnKind.Media, WarnOperation.Add)).Before, (await Balance("media")).Active));
            await Apply(WarnKind.Media, WarnOperation.Add, minutes: 1);
            var reset = await Apply(WarnKind.Media, WarnOperation.Reset, 0, minutes: 2);
            Assert.AreEqual((2L, 0L, 2L), (reset.Before, reset.After, reset.Lifetime));
            await Apply(WarnKind.Media, WarnOperation.Add, minutes: 3);
            Assert.AreEqual(new WarnBalance("media", 1, 3), await Balance("media"));
            Assert.AreEqual(1L, await _repo.GetCountAsync(-1001, WarnKind.Media, 5));
        }

        [TestMethod]
        public async Task Removing_NeverGoesBelowZero_AndWritesNothingWhenThereWasNothing()
        {
            await Apply(WarnKind.Warn, WarnOperation.Add);
            var removed = await Apply(WarnKind.Warn, WarnOperation.Remove, 3);
            Assert.AreEqual((1L, 0L), (removed.Before, removed.After));
            var nothing = await Apply(WarnKind.Warn, WarnOperation.Remove);
            Assert.AreEqual(((long?)null, 0L), (nothing.EventId, nothing.Changed));
            Assert.AreEqual(2, (await _repo.GetHistoryAsync(-1001, 5, 10)).Count);
        }

        [TestMethod]
        public async Task AMembersFirstChange_StartsFromTheOldRedisCount()
        {
            // Redis held 4 warns and 6 in total before the counts moved; nothing writes there any more.
            _redis.Hash("chat:-1001:warns")[5] = 4;
            _redis.Hash("chat:-1001:totalWarns")[5] = 6;
            Assert.AreEqual(4L, await _repo.GetCountAsync(-1001, WarnKind.Warn, 5), "read before any change");

            var added = await Apply(WarnKind.Warn, WarnOperation.Add);
            Assert.AreEqual((4L, 5L, 7L), (added.Before, added.After, added.Lifetime));

            // From then on PostgreSQL alone counts.
            _redis.Hash("chat:-1001:warns")[5] = 0;
            await Apply(WarnKind.Warn, WarnOperation.Remove);
            Assert.AreEqual(new WarnBalance("standard", 4, 7), await Balance("standard"));
            Assert.AreEqual(new WarnCounts(4, 0, 0, 7), await _repo.GetCountsAsync(-1001, 5));
        }

        [TestMethod]
        public async Task NegativeOldCounts_StartAtZero()
        {
            _redis.Hash("chat:-1001:warns")[5] = -2;
            Assert.AreEqual(1L, (await Apply(WarnKind.Warn, WarnOperation.Add)).After);
        }

        [TestMethod]
        public async Task TheSameChangeTwice_CountsOnce()
        {
            // A change queued while PostgreSQL was down whose first try went through after all.
            var first = await Apply(WarnKind.Warn, WarnOperation.Add, id: "warn:x");
            var again = await Apply(WarnKind.Warn, WarnOperation.Add, id: "warn:x");
            Assert.AreEqual((first.EventId, 1L), (again.EventId, again.After));
            Assert.AreEqual(1L, (await Balance("standard")).Active);
        }

        [TestMethod]
        public async Task ChangesAtTheSameTime_AreAllCounted()
        {
            await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Apply(WarnKind.Media, WarnOperation.Add, actor: i % 2 == 0 ? 9 : null)));
            Assert.AreEqual(new WarnBalance("media", 10, 10), await Balance("media"));
        }

        [TestMethod]
        public async Task ARecoveredCount_IsSetExactly_AndItsLifetimeOnlyByItself()
        {
            await Apply(WarnKind.Warn, WarnOperation.Add);
            var set = await Apply(WarnKind.Warn, WarnOperation.Set, 3, reason: "Restored");
            Assert.AreEqual((1L, 3L, 1L), (set.Before, set.After, set.Lifetime), "the lifetime is set on its own");
            await _repo.SetLifetimeAsync(-1001, 5, 8);
            Assert.AreEqual(new WarnBalance("standard", 3, 8), await Balance("standard"));
            await Apply(WarnKind.Warn, WarnOperation.Set, 1);
            Assert.AreEqual(new WarnBalance("standard", 1, 8), await Balance("standard"));

            var history = await _repo.GetHistoryAsync(-1001, 5, 10);
            CollectionAssert.AreEqual(new[] { ("remove", 2L), ("add", 2L), ("add", 1L) },
                history.Select(h => (h.Operation, h.Quantity)).ToArray());
        }

        [TestMethod]
        public async Task History_IsNewestFirst_WithActorAndReason_PerGroup()
        {
            var first = await Apply(WarnKind.Warn, WarnOperation.Add, minutes: 0);
            var second = await Apply(WarnKind.Warn, WarnOperation.Remove, reason: "AFK", minutes: 1);
            await Apply(WarnKind.Warn, WarnOperation.Add, chat: -1002, actor: null);

            var history = await _repo.GetHistoryAsync(-1001, 5, 10);
            CollectionAssert.AreEqual(new[] { second.EventId, first.EventId }, history.Select(h => (long?)h.EventId).ToArray());
            Assert.AreEqual(("standard", "remove", 1L, "AFK", (long?)9), (history[0].WarningType, history[0].Operation,
                history[0].Quantity, history[0].Reason, history[0].ActorId));
            Assert.AreEqual(T0.AddMinutes(1), history[0].OccurredAt);
            Assert.AreEqual(1, (await _repo.GetHistoryAsync(-1002, 5, 10)).Count);
        }

        [TestMethod]
        public async Task MaxWarnsBan_ReferencesTheWarn()
        {
            var warn = await Apply(WarnKind.Warn, WarnOperation.Add);
            var bans = new PostgresBanHistoryRepository(() => _database);
            await bans.RecordBanAsync(new BanEvent(-1001, 5, SanctionKind.Ban, "Max warns", null, T0, null, "normal", warn.EventId));

            await using var connection = await _database.OpenConnectionAsync();
            Assert.AreEqual(warn.EventId, await connection.ExecuteScalarAsync<long?>("SELECT trigger_warning_event_id FROM sanctions"));
        }

        [TestMethod]
        public async Task Trigger_FromAnotherMember_IsRefused()
        {
            // The foreign key covers group and account, so a ban cannot point at someone else's warn.
            var warn = await Apply(WarnKind.Warn, WarnOperation.Add, user: 6);
            var bans = new PostgresBanHistoryRepository(() => _database);
            await Assert.ThrowsExceptionAsync<Npgsql.PostgresException>(() =>
                bans.RecordBanAsync(new BanEvent(-1001, 5, SanctionKind.Ban, "Max warns", null, T0, null, "normal", warn.EventId)));
        }
    }
}
