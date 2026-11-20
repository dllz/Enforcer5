using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using Enforcer5.Tests.Warns;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Storage
{
    /// <summary>
    /// The mute lists in PostgreSQL, each group's import from the old Redis keys and the queue while PostgreSQL is down
    /// (rollout step 16), against a real database; skipped without the test database.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // ErrorReporter.Observer is static
    public class MuteStoreTests
    {
        private const long Chat = -1001234567890;
        private static readonly DateTime Now = DateTime.SpecifyKind(
            DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime, DateTimeKind.Utc);
        private string _connectionString;
        private PostgresDatabase _database;
        private PostgresMuteRepository _table;
        private FakeRedis _redis;
        private bool _ready;
        private MuteStore _store;
        private Action<string> _savedObserver;

        [TestInitialize]
        public async Task Setup()
        {
            _connectionString = await TestPostgres.MigratedSchemaAsync();
            _database = new PostgresDatabase(_connectionString);
            _table = new PostgresMuteRepository(() => _database);
            _redis = new FakeRedis();
            _ready = true;
            _store = NewStore();
            _savedObserver = ErrorReporter.Observer;
            ErrorReporter.Observer = _ => { };
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            ErrorReporter.Observer = _savedObserver;
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await TestPostgres.ReleaseMigratedAsync(_connectionString);
        }

        private MuteStore NewStore() =>
            new(_table, new LegacyMutes(() => _redis.Database), () => _redis.Database, "normal", () => _ready);

        private async Task<(long[] Muted, long[] Joiners, TimedRestriction[] Temp)> All() =>
            ((await _store.GetMutedAsync(Chat)).ToArray(), (await _store.GetMutedJoinersAsync(Chat)).ToArray(),
                (await _store.GetTempMutedAsync(Chat, Now)).ToArray());

        // ------------------------------------------------------------------ the lists

        [TestMethod]
        public async Task NewGroup_HasNoMutes()
        {
            var (muted, joiners, temp) = await All();
            Assert.AreEqual(0, muted.Length + joiners.Length + temp.Length);
        }

        [TestMethod]
        public async Task Mutes_AreListedInTheOrderTheyWereMade()
        {
            await _store.MarkMutedAsync(Chat, 2);
            await _store.MarkMutedAsync(Chat, 1);
            CollectionAssert.AreEquivalent(new long[] { 1, 2 }, (await _store.GetMutedAsync(Chat)).ToArray());
        }

        [TestMethod]
        public async Task Tempmute_IsListedWithItsEnd_AndEndedOnesAreDeleted()
        {
            await _store.MarkTempMutedAsync(Chat, 1, Now.AddMinutes(-1));
            await _store.MarkTempMutedAsync(Chat, 2, Now);
            await _store.MarkTempMutedAsync(Chat, 3, Now.AddHours(2));

            var temp = await _store.GetTempMutedAsync(Chat, Now);
            CollectionAssert.AreEqual(new[] { new TimedRestriction(3, Now.AddHours(2)) }, temp.ToArray());
            await using var connection = await _database.OpenConnectionAsync();
            Assert.AreEqual(1L, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM group_mutes"),
                "Telegram lifts a tempmute by itself; nothing else removes the row");
        }

        [TestMethod]
        public async Task LatestMute_Wins()
        {
            await _store.MarkJoinerMutedAsync(Chat, 1);
            await _store.MarkTempMutedAsync(Chat, 1, Now.AddHours(1));
            var (muted, joiners, temp) = await All();
            Assert.AreEqual(0, muted.Length + joiners.Length, "a tempmute replaces the join mute");
            Assert.AreEqual(1, temp.Length);

            await _store.MarkMutedAsync(Chat, 1);
            (muted, _, temp) = await All();
            CollectionAssert.AreEqual(new long[] { 1 }, muted);
            Assert.AreEqual(0, temp.Length, "a mute replaces the tempmute");

            await _store.MarkTempMutedAsync(Chat, 1, Now.AddHours(1));
            (muted, _, temp) = await All();
            Assert.AreEqual(0, muted.Length, "and the other way round");
            Assert.AreEqual(1, temp.Length);
        }

        [TestMethod]
        public async Task JoinMute_DoesNotDemoteAnAdminsMute_ButReplacesATempmute()
        {
            // Otherwise /unmutenewjoiners would unmute someone an admin muted, who left and came back.
            await _store.MarkMutedAsync(Chat, 1);
            await _store.MarkJoinerMutedAsync(Chat, 1);
            await _store.MarkTempMutedAsync(Chat, 2, Now.AddHours(1));
            await _store.MarkJoinerMutedAsync(Chat, 2);

            var (muted, joiners, temp) = await All();
            CollectionAssert.AreEqual(new long[] { 1 }, muted);
            CollectionAssert.AreEqual(new long[] { 2 }, joiners);
            Assert.AreEqual(0, temp.Length);
        }

        [TestMethod]
        public async Task Clear_ForgetsEveryKindOfMute_AndGroupsAreSeparate()
        {
            await _store.MarkMutedAsync(Chat, 1);
            await _store.MarkJoinerMutedAsync(Chat, 2);
            await _store.MarkTempMutedAsync(Chat, 3, Now.AddHours(1));
            await _store.MarkMutedAsync(Chat, 4);
            await _store.MarkMutedAsync(-100999, 1);

            foreach (var user in new long[] { 1, 2, 3 }) await _store.ClearAsync(Chat, user);

            var (muted, joiners, temp) = await All();
            CollectionAssert.AreEqual(new long[] { 4 }, muted);
            Assert.AreEqual(0, joiners.Length + temp.Length);
            CollectionAssert.AreEqual(new long[] { 1 }, (await _store.GetMutedAsync(-100999)).ToArray());
        }

        // ------------------------------------------------------------------ the import

        private void SeedRedis()
        {
            _redis.Set(LegacyMutes.MutedKey(Chat)).Add(1);
            _redis.Set(LegacyMutes.MutedKey(Chat)).Add("garbage");
            _redis.Set(LegacyMutes.JoinersKey(Chat)).Add(2);
            _redis.Set(LegacyMutes.JoinersKey(Chat)).Add(1); // in two lists: the mute for good wins
            _redis.Hash(LegacyMutes.TempMutedKey(Chat))[3] = new DateTimeOffset(Now.AddHours(1)).ToUnixTimeSeconds();
            _redis.Hash(LegacyMutes.TempMutedKey(Chat))[4] = new DateTimeOffset(Now.AddHours(-1)).ToUnixTimeSeconds();
        }

        [TestMethod]
        public async Task OldKeys_AreImportedOnTheGroupsFirstUse_AndLeftAlone()
        {
            SeedRedis();

            var (muted, joiners, temp) = await All();
            CollectionAssert.AreEqual(new long[] { 1 }, muted);
            CollectionAssert.AreEqual(new long[] { 2 }, joiners);
            CollectionAssert.AreEqual(new[] { new TimedRestriction(3, Now.AddHours(1)) }, temp);
            Assert.AreEqual(2, _redis.Set(LegacyMutes.MutedKey(Chat)).Count, "the old keys stay for a rollback");
        }

        [TestMethod]
        public async Task Import_RunsOnce_SoAnOldEntryNeverUndoesALaterChange()
        {
            SeedRedis();
            await _store.ClearAsync(Chat, 1); // the first change imports first, then clears

            // A restart: another process, nothing in memory.
            _store = NewStore();
            _redis.Set(LegacyMutes.MutedKey(Chat)).Add(5);
            var (muted, _, _) = await All();
            Assert.AreEqual(0, muted.Length, "imported once ever, before the change");
        }

        [TestMethod]
        public async Task Import_KeepsARowWrittenAlready()
        {
            // Another edition wrote this one in the meantime (it imports first too, so only by a race).
            await _table.TempMuteAsync(Chat, 1, Now.AddDays(1), Now);
            SeedRedis();
            var (muted, _, temp) = await All();
            Assert.AreEqual(0, muted.Length, "their row stays");
            Assert.AreEqual(Now.AddDays(1), temp.Single(t => t.UserId == 1).UntilUtc);
        }

        // ------------------------------------------------------------------ PostgreSQL down

        [TestMethod]
        public async Task WhilePostgresIsDown_ChangesQueueInRedis_AndAreWrittenInOrderOnceBack()
        {
            _ready = false;
            await _store.MarkMutedAsync(Chat, 1);
            await _store.MarkTempMutedAsync(Chat, 2, Now.AddHours(1));
            await _store.ClearAsync(Chat, 1);
            Assert.AreEqual(3, _redis.Hash(_store.QueueKey).Count);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _store.GetMutedAsync(Chat),
                "the list says it failed rather than showing nothing");

            // A restart before PostgreSQL is back: the queue survives it.
            _store = NewStore();
            await _store.LoadQueueAsync();
            Assert.AreEqual(3, _store.Queued);
            await _store.MarkJoinerMutedAsync(Chat, 3);

            _ready = true;
            var (muted, joiners, temp) = await All();
            Assert.AreEqual(0, muted.Length, "the clear came after the mute");
            CollectionAssert.AreEqual(new long[] { 3 }, joiners);
            Assert.AreEqual(2L, temp.Single().UserId);
            Assert.AreEqual(0, _redis.Hash(_store.QueueKey).Count);
            Assert.AreEqual(0, _store.Queued);
        }

        [TestMethod]
        public async Task QueuedChanges_ImportTheGroupFirst()
        {
            SeedRedis();
            _ready = false;
            await _store.ClearAsync(Chat, 2);
            _ready = true;
            await _store.DrainAsync();

            var (muted, joiners, _) = await All();
            CollectionAssert.AreEqual(new long[] { 1 }, muted);
            Assert.AreEqual(0, joiners.Length, "imported, then the queued clear applied on top");
        }
    }
}
