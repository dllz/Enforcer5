using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Reports (PostgreSQL rollout step 6) driven end to end: a member reports a message, the admin
    /// acts from the buttons in their private chat with the bot, then solves it. Needs the test
    /// database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class ReportScenarios
    {
        private sealed class Report
        {
            public long Id { get; set; }
            public long? ReporterTelegramId { get; set; }
            public string ReporterDisplay { get; set; }
            public long? TargetTelegramId { get; set; }
            public long ReportMessageId { get; set; }
            public long? TargetMessageId { get; set; }
            public string Status { get; set; }
            public string ResolutionAction { get; set; }
            public long? SolverTelegramId { get; set; }
        }

        private sealed class Action
        {
            public string Name { get; set; }
            public long? ActorTelegramId { get; set; }
        }

        private static async Task<Report[]> Reports(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Report>(@"
SELECT r.report_id AS Id, reporter.telegram_user_id AS ReporterTelegramId, r.reporter_display AS ReporterDisplay,
       target.telegram_user_id AS TargetTelegramId, r.report_message_id AS ReportMessageId,
       r.target_message_id AS TargetMessageId, r.status AS Status, r.resolution_action AS ResolutionAction,
       solver.telegram_user_id AS SolverTelegramId
FROM reports r
LEFT JOIN telegram_accounts reporter ON reporter.account_id = r.reporter_account_id
LEFT JOIN telegram_accounts target ON target.account_id = r.target_account_id
LEFT JOIN user_identity_links sl ON sl.user_id = r.solver_user_id AND sl.unlinked_at IS NULL
LEFT JOIN telegram_accounts solver ON solver.account_id = sl.account_id
ORDER BY r.report_id")).ToArray();
        }

        private static async Task<Action[]> Actions(HarnessChat chat, long reportId)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Action>(@"
SELECT ra.action AS Name, actor.telegram_user_id AS ActorTelegramId
FROM report_actions ra
LEFT JOIN user_identity_links l ON l.user_id = ra.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id
WHERE ra.report_id = @reportId
ORDER BY ra.report_action_id", new { reportId })).ToArray();
        }

        private static async Task<long?[]> Linked(HarnessChat chat, string table)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<long?>($"SELECT report_id FROM {table} ORDER BY 1")).ToArray();
        }

        // A group with a link set (/setlink). Without one, a private group's report menu holds a null
        // button that is reported as an error on every report (a separate, pre-existing bug).
        private static async Task<HarnessChat> StartAsync()
        {
            var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}links", "link"] = "https://t.me/+harness";
            return chat;
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task Report_DeleteWarnKick_ThenSolve()
        {
            await using var chat = await StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "max"] = "3";
            var admin = chat.Add("Admin", MemberState.Administrator, username: "theadmin");
            var reporter = chat.Add("Reporter");
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");

            var reportMessage = await chat.SendAsync(reporter, "/report", replyTo: spam);

            var report = (await Reports(chat)).Single();
            Assert.AreEqual(((long?)reporter.Id, "Reporter", (long?)spammer.Id, (long)reportMessage.MessageId, (long?)spam.MessageId, "open"),
                (report.ReporterTelegramId, report.ReporterDisplay, report.TargetTelegramId, report.ReportMessageId,
                    report.TargetMessageId, report.Status));

            var dm = chat.Telegram.LastMessageTo(admin.Id);
            await chat.PressAsync(admin, dm, "delflag:");
            await chat.PressAsync(admin, dm, "warnflag:");
            await chat.PressAsync(admin, dm, "kickflag:");
            Assert.AreEqual("open", (await Reports(chat)).Single().Status, "Acting on a report does not solve it.");

            await chat.PressAsync(admin, dm, "solveflag:");

            report = (await Reports(chat)).Single();
            Assert.AreEqual(("resolved", "solved", (long?)admin.Id), (report.Status, report.ResolutionAction, report.SolverTelegramId));
            var actions = await Actions(chat, report.Id);
            CollectionAssert.AreEqual(new[] { "deleted", "warned", "kicked" }, actions.Select(a => a.Name).ToArray());
            Assert.IsTrue(actions.All(a => a.ActorTelegramId == admin.Id));
            CollectionAssert.AreEqual(new long?[] { report.Id }, await Linked(chat, "warning_events"));
            CollectionAssert.AreEqual(new long?[] { report.Id }, await Linked(chat, "sanctions"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ReportBan_IsLogged_AndSolvedWithTheCommand()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var reporter = chat.Add("Reporter");
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");
            var reportMessage = await chat.SendAsync(reporter, "@admin spam", replyTo: spam);

            await chat.PressAsync(admin, chat.Telegram.LastMessageTo(admin.Id), "banflag:");
            await chat.SendAsync(admin, $"/solved {reportMessage.MessageId}");

            var report = (await Reports(chat)).Single();
            Assert.AreEqual(("resolved", (long?)admin.Id), (report.Status, report.SolverTelegramId));
            CollectionAssert.AreEqual(new[] { "banned" }, (await Actions(chat, report.Id)).Select(a => a.Name).ToArray());
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, spammer.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ReportWithoutAReply_HasNoTarget()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var reporter = chat.Add("Reporter");

            await chat.SendAsync(reporter, "/report something is off");

            var report = (await Reports(chat)).Single();
            Assert.AreEqual(((long?)reporter.Id, (long?)null, (long?)null, "open"),
                (report.ReporterTelegramId, report.TargetTelegramId, report.TargetMessageId, report.Status));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ButtonFromBeforeTheHistory_FindsTheOpenReportAgainstTheMember()
        {
            // Buttons already in admins' chats carry no report id.
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var reporter = chat.Add("Reporter");
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");
            await chat.SendAsync(reporter, "/report", replyTo: spam);

            await chat.PressDataAsync(admin, chat.Telegram.LastMessageTo(admin.Id), $"kickflag:{chat.ChatId}:{spammer.Id}");

            var report = (await Reports(chat)).Single();
            CollectionAssert.AreEqual(new[] { "kicked" }, (await Actions(chat, report.Id)).Select(a => a.Name).ToArray());
            CollectionAssert.AreEqual(new long?[] { report.Id }, await Linked(chat, "sanctions"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheSameMessageReportedTwice_IsOneReport()
        {
            await using var chat = await StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            var first = chat.Add("First");
            var second = chat.Add("Second");
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");

            await chat.SendAsync(first, "/report", replyTo: spam);
            await chat.SendAsync(second, "/report", replyTo: spam);

            Assert.AreEqual(1, (await Reports(chat)).Length);
            AssertNoErrors(chat);
        }
    }
}
