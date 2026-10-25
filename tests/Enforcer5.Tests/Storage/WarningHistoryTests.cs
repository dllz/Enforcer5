using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Storage
{
    /// <summary>WarnHistory and its ordering with the ban history, against fakes.</summary>
    [TestClass]
    [DoNotParallelize] // HistoryWriter and Repositories are static
    public class WarnHistoryWriterTests
    {
        private sealed class FakeWarnings : IWarningHistoryRepository
        {
            public readonly List<string> Calls;
            private long _nextId = 100;
            public FakeWarnings(List<string> calls) => Calls = calls;

            public Task<long> RecordAsync(WarnEvent warn)
            {
                var id = _nextId++;
                Calls.Add($"warn#{id} {warn.Kind} {warn.Operation} {warn.Quantity} active {warn.ActiveAfter} by {warn.ActorId}");
                return Task.FromResult(id);
            }

            public Task<IReadOnlyList<WarnRecord>> GetHistoryAsync(long chatId, long userId, int limit) => throw new NotSupportedException();
            public Task<IReadOnlyList<WarnBalance>> GetBalancesAsync(long chatId, long userId) => throw new NotSupportedException();
        }

        private sealed class FakeBans : IBanHistoryRepository
        {
            public readonly List<string> Calls;
            public FakeBans(List<string> calls) => Calls = calls;

            public Task RecordBanAsync(BanEvent ban)
            {
                Calls.Add($"ban {ban.Reason} trigger {ban.TriggerWarningEventId}");
                return Task.CompletedTask;
            }

            public Task<bool> RecordUnbanAsync(UnbanEvent unban) => Task.FromResult(true);
            public Task<IReadOnlyList<BanRecord>> GetHistoryAsync(long chatId, long userId, int limit) => throw new NotSupportedException();
            public Task<IReadOnlyList<BanRecord>> GetSanctionsAsync(long chatId, long userId, int limit) => throw new NotSupportedException();
            public Task<bool> RecordUnmuteAsync(UnmuteEvent unmute) => Task.FromResult(true);
        }

        private IWarningHistoryRepository _savedWarnings;
        private IBanHistoryRepository _savedBans;
        private Func<bool> _savedEnabled;
        private List<string> _calls;

        [TestInitialize]
        public void Setup()
        {
            _savedWarnings = Repositories.WarningHistory;
            _savedBans = Repositories.BanHistory;
            _savedEnabled = HistoryWriter.Enabled;
            _calls = new List<string>();
            Repositories.WarningHistory = new FakeWarnings(_calls);
            Repositories.BanHistory = new FakeBans(_calls);
            HistoryWriter.Enabled = () => true;
        }

        [TestCleanup]
        public void Cleanup()
        {
            Repositories.WarningHistory = _savedWarnings;
            Repositories.BanHistory = _savedBans;
            HistoryWriter.Enabled = _savedEnabled;
        }

        [TestMethod]
        public async Task MaxWarnsBan_PointsAtTheWarnWrittenBeforeIt()
        {
            var warned = WarnHistory.Added(-100, 1, WarnKind.Warn, 1, 3, 3, 7);
            BanHistory.Banned(-100, 1, SanctionKind.Ban, "Max warns", null, trigger: warned);
            WarnHistory.Reset(-100, 1, WarnKind.Warn, 3, null, "Max warns");
            await HistoryWriter.FlushAsync();

            CollectionAssert.AreEqual(new[]
            {
                "warn#100 Warn Add 1 active 3 by 7",
                "ban Max warns trigger 100",
                "warn#101 Warn Reset 3 active 0 by ",
            }, _calls);
        }

        [TestMethod]
        public async Task NothingChanged_WritesNothing()
        {
            WarnHistory.Reset(-100, 1, WarnKind.Media, 0, 7, null);
            WarnHistory.Removed(-100, 1, WarnKind.Warn, 0, 0, 7, null);
            await HistoryWriter.FlushAsync();
            Assert.AreEqual(0, _calls.Count);
        }

        [TestMethod]
        public async Task Removal_IsRecordedWithTheCountTakenOff()
        {
            var redis = new Warns.FakeRedis();
            redis.Hash("chat:-100:warns")[1] = 2;

            await Enforcer5.WarnRemovals.ApplyAsync(new RedisWarnRepository(() => redis.Database),
                new RedisWarnReasonRepository(() => redis.Database), -100,
                new WarnChange(WarnKind.Warn, false, 1), "AFK", 7);
            await HistoryWriter.FlushAsync();

            CollectionAssert.AreEqual(new[] { "warn#100 Warn Remove 1 active 1 by 7" }, _calls);
        }

        [TestMethod]
        public async Task ResetFromTheWarnButton_AlsoResetsMediaWarns()
        {
            var redis = new Warns.FakeRedis();
            redis.Hash("chat:-100:warns")[1] = 2;
            redis.Hash("chat:-100:mediawarn")[1] = 1;

            await Enforcer5.WarnRemovals.ApplyAsync(new RedisWarnRepository(() => redis.Database),
                new RedisWarnReasonRepository(() => redis.Database), -100,
                new WarnChange(WarnKind.Warn, true, 1, AlsoMediaWarns: true), null, 7);
            await HistoryWriter.FlushAsync();

            CollectionAssert.AreEqual(new[]
            {
                "warn#100 Warn Reset 2 active 0 by 7",
                "warn#101 Media Reset 1 active 0 by 7",
            }, _calls);
        }
    }

    /// <summary>The SQL against a real PostgreSQL; skipped without the test database.</summary>
    [TestClass]
    public class PostgresWarningHistoryRepositoryTests
    {
        private string _connectionString;
        private PostgresDatabase _database;
        private PostgresWarningHistoryRepository _repo;
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        [TestInitialize]
        public async Task Setup()
        {
            _connectionString = await TestPostgres.CreateSchemaAsync();
            await SchemaMigrationRunner.ApplyAsync(_connectionString);
            _database = new PostgresDatabase(_connectionString);
            _repo = new PostgresWarningHistoryRepository(() => _database);
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await TestPostgres.DropSchemaAsync(_connectionString);
        }

        private Task<long> Record(WarnKind kind, WarnOperation op, long quantity, long active, long? lifetime = null,
            long chat = -1001, long user = 5, long? actor = 9, string reason = null, int minutes = 0) =>
            _repo.RecordAsync(new WarnEvent(chat, user, kind, op, quantity, active, lifetime, reason, actor, T0.AddMinutes(minutes)));

        private async Task<WarnBalance> Balance(string type, long chat = -1001) =>
            (await _repo.GetBalancesAsync(chat, 5)).SingleOrDefault(b => b.WarningType == type);

        [TestMethod]
        public async Task Balance_CopiesRedis_AndLifetimeCountsAdds()
        {
            await Record(WarnKind.Media, WarnOperation.Add, 1, 1, minutes: 0);
            await Record(WarnKind.Media, WarnOperation.Add, 1, 2, minutes: 1);
            await Record(WarnKind.Media, WarnOperation.Reset, 2, 0, minutes: 2);
            await Record(WarnKind.Media, WarnOperation.Add, 1, 1, minutes: 3);
            Assert.AreEqual(new WarnBalance("media", 1, 3), await Balance("media"));
        }

        [TestMethod]
        public async Task FirstChange_ForAMemberRedisAlreadyCounts_StartsFromRedis()
        {
            // Redis held 4 warns and 6 in total before the history started.
            await Record(WarnKind.Warn, WarnOperation.Add, 1, 5, lifetime: 7);
            Assert.AreEqual(new WarnBalance("standard", 5, 7), await Balance("standard"));

            await Record(WarnKind.Warn, WarnOperation.Remove, 1, 4);
            Assert.AreEqual(new WarnBalance("standard", 4, 7), await Balance("standard"));
        }

        [TestMethod]
        public async Task NegativeRedisCounts_AreStoredAsZero()
        {
            await Record(WarnKind.Warn, WarnOperation.Add, 1, -2, lifetime: -1);
            Assert.AreEqual(new WarnBalance("standard", 0, 0), await Balance("standard"));
        }

        [TestMethod]
        public async Task History_IsNewestFirst_WithActorAndReason_PerGroup()
        {
            var first = await Record(WarnKind.Warn, WarnOperation.Add, 1, 1, minutes: 0);
            var second = await Record(WarnKind.Warn, WarnOperation.Remove, 1, 0, reason: "AFK", minutes: 1);
            await Record(WarnKind.Warn, WarnOperation.Add, 1, 1, chat: -1002, actor: null);

            var history = await _repo.GetHistoryAsync(-1001, 5, 10);
            CollectionAssert.AreEqual(new[] { second, first }, history.Select(h => h.EventId).ToArray());
            Assert.AreEqual(("standard", "remove", 1L, "AFK", (long?)9), (history[0].WarningType, history[0].Operation,
                history[0].Quantity, history[0].Reason, history[0].ActorId));
            Assert.AreEqual(T0.AddMinutes(1), history[0].OccurredAt);
            Assert.AreEqual(1, (await _repo.GetHistoryAsync(-1002, 5, 10)).Count);
        }

        [TestMethod]
        public async Task ZeroQuantity_IsRefused()
        {
            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                () => Record(WarnKind.Warn, WarnOperation.Reset, 0, 0));
        }

        [TestMethod]
        public async Task MaxWarnsBan_ReferencesTheWarn()
        {
            var warn = await Record(WarnKind.Warn, WarnOperation.Add, 1, 3, lifetime: 3);
            var bans = new PostgresBanHistoryRepository(() => _database);
            await bans.RecordBanAsync(new BanEvent(-1001, 5, SanctionKind.Ban, "Max warns", null, T0, null, "normal", warn));

            await using var connection = await _database.OpenConnectionAsync();
            Assert.AreEqual(warn, await connection.ExecuteScalarAsync<long?>("SELECT trigger_warning_event_id FROM sanctions"));
        }

        [TestMethod]
        public async Task Trigger_FromAnotherMember_IsRefused()
        {
            // The foreign key covers group and account, so a ban cannot point at someone else's warn.
            var warn = await Record(WarnKind.Warn, WarnOperation.Add, 1, 3, user: 6);
            var bans = new PostgresBanHistoryRepository(() => _database);
            await Assert.ThrowsExceptionAsync<Npgsql.PostgresException>(() =>
                bans.RecordBanAsync(new BanEvent(-1001, 5, SanctionKind.Ban, "Max warns", null, T0, null, "normal", warn)));
        }
    }
}
