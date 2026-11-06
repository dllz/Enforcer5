using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Tests.Warns;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Storage
{
    /// <summary>
    /// The tempban schedule in PostgreSQL and its queue in Redis (rollout step 13), against a real database; skipped
    /// without the test database.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // ErrorReporter.Observer is static
    public class TempbanScheduleTests
    {
        private const long Chat = -1001;
        private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        private string _connectionString;
        private PostgresDatabase _database;
        private PostgresTempbanRepository _normal;
        private FakeRedis _redis;
        private bool _ready;
        private Action<string> _savedObserver;

        [TestInitialize]
        public async Task Setup()
        {
            _connectionString = await TestPostgres.MigratedSchemaAsync();
            _database = new PostgresDatabase(_connectionString);
            _normal = new PostgresTempbanRepository(() => _database, "normal");
            _redis = new FakeRedis();
            _ready = true;
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

        private TempbanStore Store(string index = "tempbanned") =>
            new(_normal, new LegacyTempbans(() => _redis.Database, index), () => _redis.Database, () => _ready);

        private static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

        // ------------------------------------------------------------------ the repository

        [TestMethod]
        public async Task ASecondTempban_ReplacesTheFirst_AndTwoEndingInTheSameSecondBothStay()
        {
            await _normal.ScheduleAsync(Chat, 1, Now.AddHours(1), Now);
            await _normal.ScheduleAsync(Chat, 1, Now.AddHours(3), Now);
            await _normal.ScheduleAsync(Chat, 2, Now.AddHours(3), Now);

            var active = await _normal.GetActiveAsync(Chat, Now);
            CollectionAssert.AreEqual(new[] { (1L, Now.AddHours(3)), (2L, Now.AddHours(3)) },
                active.Select(a => (a.UserId, a.UntilUtc)).ToArray());
        }

        [TestMethod]
        public async Task Due_ClaimsOnlyOnce_UntilTheClaimIsOld()
        {
            await _normal.ScheduleAsync(Chat, 1, Now.AddMinutes(-1), Now.AddHours(-1));
            await _normal.ScheduleAsync(Chat, 2, Now.AddMinutes(5), Now.AddHours(-1));

            var due = await _normal.DueAsync(Now);
            Assert.AreEqual((Chat, 1L), (due.Single().ChatId, due.Single().UserId));
            Assert.AreEqual(0, (await _normal.DueAsync(Now)).Count, "claimed");
            Assert.AreEqual(1, (await _normal.DueAsync(Now.AddMinutes(11))).Count(d => d.UserId == 1), "a claim left behind is taken again");
        }

        [TestMethod]
        public async Task Remove_LeavesATempbanGivenSince()
        {
            await _normal.ScheduleAsync(Chat, 1, Now.AddMinutes(-1), Now.AddHours(-1));
            var due = (await _normal.DueAsync(Now)).Single();
            await _normal.ScheduleAsync(Chat, 1, Now.AddDays(1), Now); // tempbanned again while the unban went out

            await _normal.RemoveAsync(due);

            Assert.AreEqual(Now.AddDays(1), (await _normal.GetActiveAsync(Chat, Now)).Single().UntilUtc);
            await _normal.RemoveAsync(new TempbanEntry(Unix(Now.AddDays(1)).ToString(), Chat, 1));
            Assert.AreEqual(0, (await _normal.GetActiveAsync(Chat, Now)).Count);
        }

        [TestMethod]
        public async Task EachEdition_HasItsOwnSchedule()
        {
            var premium = new PostgresTempbanRepository(() => _database, "premium");
            await premium.ScheduleAsync(Chat, 1, Now.AddHours(1), Now);
            await _normal.ScheduleAsync(Chat, 1, Now.AddHours(2), Now);
            await _normal.CancelAsync(Chat, 1);

            Assert.AreEqual(0, (await _normal.OfUserAsync(1, Now)).Count);
            Assert.AreEqual(1, (await premium.OfUserAsync(1, Now)).Count);
            Assert.AreEqual(0, (await _normal.DueAsync(Now.AddDays(1))).Count);
        }

        // ------------------------------------------------------------------ the import

        [TestMethod]
        public async Task TheOldSchedule_IsImportedOnce_TheLatestPerMember_FarEndsAsTheyAre()
        {
            var hash = _redis.Hash("tempbanned");
            hash[Unix(Now.AddHours(1))] = $"{Chat}:1:Name:Group";
            hash[Unix(Now.AddHours(5))] = $"{Chat}:1:Name:Group"; // the same member again: the later one counts
            hash[Unix(new DateTime(2969, 1, 1, 0, 0, 0, DateTimeKind.Utc))] = $"{Chat}:2:Far:Group";
            hash["not-a-time"] = $"{Chat}:3:x:y";
            hash[Unix(Now.AddHours(2))] = "garbage";
            _redis.Hash("tempbannedPremium")[Unix(Now.AddHours(1))] = $"{Chat}:4:Premium:Group";

            var store = Store();
            await store.StartUpAsync(Now);

            var active = await _normal.GetActiveAsync(Chat, Now);
            CollectionAssert.AreEqual(new[] { (1L, Now.AddHours(5)), (2L, new DateTime(2969, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
                active.Select(a => (a.UserId, a.UntilUtc)).ToArray());

            // Lifted since, and the bot restarts: the import does not bring it back.
            await store.CancelAsync(Chat, 1);
            await Store().StartUpAsync(Now);
            Assert.AreEqual(1, (await _normal.GetActiveAsync(Chat, Now)).Count);
            await using var connection = await _database.OpenConnectionAsync();
            Assert.AreEqual("3 entries, 2 imported",
                await connection.ExecuteScalarAsync<string>("SELECT details FROM storage_imports WHERE name = 'tempbans:normal'"));
        }

        [TestMethod]
        public async Task BeforeTheImport_ChangesQueue_SoTheImportCannotUndoThem()
        {
            _redis.Hash("tempbanned")[Unix(Now.AddHours(1))] = $"{Chat}:1:Name:Group";
            var store = Store();

            await store.CancelAsync(Chat, 1); // banned for good, say, before the import ran
            Assert.AreEqual(1, store.Queued);
            await store.StartUpAsync(Now);
            await store.DrainAsync();

            Assert.AreEqual(0, (await _normal.GetActiveAsync(Chat, Now)).Count);
        }

        // ------------------------------------------------------------------ the queue

        [TestMethod]
        public async Task WhilePostgresIsDown_ChangesQueue_TheCheckWaits_AndAllIsWrittenInOrderOnceItIsBack()
        {
            var store = Store();
            await store.StartUpAsync(Now);
            await store.ScheduleAsync(Chat, 1, Now.AddMinutes(-5), Now.AddHours(-1));
            _ready = false;

            await store.ScheduleAsync(Chat, 2, Now.AddHours(1), Now);
            await store.CancelAsync(Chat, 1);
            Assert.AreEqual(2, store.Queued);
            Assert.AreEqual(2, _redis.Hash(store.QueueKey).Count);
            Assert.AreEqual(0, (await store.DueAsync(Now)).Count, "nothing is lifted while changes wait");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.GetActiveAsync(Chat, Now));

            _ready = true;
            Assert.AreEqual(0, (await store.DueAsync(Now)).Count, "the queued cancel went first");
            Assert.AreEqual(0, store.Queued);
            Assert.AreEqual(0, _redis.Hash(store.QueueKey).Count);
            Assert.AreEqual(2L, (await store.GetActiveAsync(Chat, Now)).Single().UserId);
        }

        [TestMethod]
        public async Task AfterARestart_TheQueueIsPickedUp()
        {
            var store = Store();
            await store.StartUpAsync(Now);
            _ready = false;
            await store.ScheduleAsync(Chat, 1, Now.AddHours(1), Now);

            var restarted = Store();
            _ready = true;
            await restarted.StartUpAsync(Now);
            Assert.AreEqual(1, restarted.Queued);
            await restarted.DrainAsync();
            Assert.AreEqual(1, (await _normal.GetActiveAsync(Chat, Now)).Count);
        }

        [TestMethod]
        public async Task AJoinOfSomeoneNotScheduled_DoesNotTouchTheDatabase()
        {
            var store = Store();
            await store.StartUpAsync(Now);
            _ready = false; // would queue if it tried

            await store.CancelAsync(Chat, 99);

            Assert.AreEqual(0, store.Queued);
        }
    }
}
