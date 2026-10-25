using System;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Storage
{
    /// <summary>The report history SQL against a real PostgreSQL; skipped without the test database.</summary>
    [TestClass]
    public class PostgresReportHistoryRepositoryTests
    {
        private string _connectionString;
        private PostgresDatabase _database;
        private PostgresReportHistoryRepository _repo;
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        [TestInitialize]
        public async Task Setup()
        {
            _connectionString = await TestPostgres.CreateSchemaAsync();
            await SchemaMigrationRunner.ApplyAsync(_connectionString);
            _database = new PostgresDatabase(_connectionString);
            _repo = new PostgresReportHistoryRepository(() => _database);
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await TestPostgres.DropSchemaAsync(_connectionString);
        }

        private Task<long> Report(long reportMessage, long? target = 5, long? targetMessage = 100, long chat = -1001,
            long? reporter = 7, int minutes = 0) =>
            _repo.RecordReportAsync(new ReportEvent(chat, reportMessage, reporter, "Reporter", target, "Target",
                target.HasValue ? targetMessage : null, T0.AddMinutes(minutes)));

        [TestMethod]
        public async Task Report_Actions_Solve()
        {
            var id = await Report(101);
            await _repo.RecordActionAsync(id, ReportAction.Deleted, 9, T0.AddMinutes(1));
            await _repo.RecordActionAsync(id, ReportAction.Warned, 9, T0.AddMinutes(2));
            Assert.IsTrue(await _repo.RecordSolvedAsync(-1001, 101, 9, "Admin", T0.AddMinutes(3)));

            var report = (await _repo.GetReportsAsync(-1001, 10)).Single();
            Assert.AreEqual((101L, (long?)7, (long?)5, (long?)100, "resolved", "solved", (long?)9, "Admin"),
                (report.ReportMessageId, report.ReporterId, report.TargetId, report.TargetMessageId, report.Status,
                    report.ResolutionAction, report.SolverId, report.SolverDisplay));
            Assert.AreEqual(T0.AddMinutes(3), report.ResolvedAt);
            CollectionAssert.AreEqual(new[] { ("deleted", (long?)9), ("warned", (long?)9) }, report.Actions.ToArray());
        }

        [TestMethod]
        public async Task SolvingTwice_OrAnUnknownReport_WritesNothing()
        {
            await Report(101);
            Assert.IsTrue(await _repo.RecordSolvedAsync(-1001, 101, 9, "Admin", T0.AddMinutes(1)));
            Assert.IsFalse(await _repo.RecordSolvedAsync(-1001, 101, 8, "Other", T0.AddMinutes(2)));
            Assert.IsFalse(await _repo.RecordSolvedAsync(-1001, 555, 8, "Other", T0));
            Assert.IsFalse(await _repo.RecordSolvedAsync(-1009, 101, 8, "Other", T0));
            Assert.AreEqual((long?)9, (await _repo.GetReportsAsync(-1001, 10)).Single().SolverId);
        }

        [TestMethod]
        public async Task SecondOpenReportOnTheSameMessage_ReturnsTheFirst()
        {
            var first = await Report(101);
            Assert.AreEqual(first, await Report(102, reporter: 8));
            await _repo.RecordSolvedAsync(-1001, 101, 9, "Admin", T0.AddMinutes(1));
            // Once solved, the message can be reported again.
            Assert.AreNotEqual(first, await Report(103, reporter: 8, minutes: 2));
        }

        [TestMethod]
        public async Task Find_ByReportMessage_ThenNewestOpenAgainstTheMember_ThenByMessage()
        {
            var old = await Report(101, targetMessage: 100);
            var newer = await Report(103, targetMessage: 102, minutes: 1);
            var other = await Report(105, target: 6, targetMessage: 104, minutes: 2);
            var noTarget = await Report(107, target: null, minutes: 3);

            Assert.AreEqual(old, await _repo.FindAsync(new ReportLookup(-1001, 101, 5, null)));
            Assert.AreEqual(newer, await _repo.FindAsync(new ReportLookup(-1001, null, 5, null)));
            Assert.AreEqual(other, await _repo.FindAsync(new ReportLookup(-1001, null, null, 104)));
            Assert.AreEqual(noTarget, await _repo.FindAsync(new ReportLookup(-1001, null, null, 107)));
            Assert.IsNull(await _repo.FindAsync(new ReportLookup(-1001, null, 99, null)));
            Assert.IsNull(await _repo.FindAsync(new ReportLookup(-1009, 101, null, null)));
        }

        [TestMethod]
        public async Task ReportWithoutAPerson_KeepsTheDisplayOnly()
        {
            // Sent as a channel or an anonymous admin: no account, just the name shown.
            var id = await Report(101, target: null, reporter: null);
            var report = (await _repo.GetReportsAsync(-1001, 10)).Single();
            Assert.AreEqual((id, (long?)null, (long?)null), (report.ReportId, report.ReporterId, report.TargetId));
        }

        [TestMethod]
        public async Task SanctionsAndWarns_ReferenceTheReport()
        {
            var id = await Report(101);
            await new PostgresBanHistoryRepository(() => _database).RecordBanAsync(
                new BanEvent(-1001, 5, SanctionKind.Kick, "Reported", 9, T0, null, "normal", ReportId: id));
            await new PostgresWarningHistoryRepository(() => _database).RecordAsync(
                new WarnEvent(-1001, 5, Enforcer5.Models.WarnKind.Warn, WarnOperation.Add, 1, 1, 1, null, 9, T0, id));

            await using var connection = await _database.OpenConnectionAsync();
            Assert.AreEqual(id, await Dapper.SqlMapper.ExecuteScalarAsync<long>(connection, "SELECT report_id FROM sanctions"));
            Assert.AreEqual(id, await Dapper.SqlMapper.ExecuteScalarAsync<long>(connection, "SELECT report_id FROM warning_events"));
        }
    }
}
