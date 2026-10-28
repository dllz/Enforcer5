using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 7: reports to the admins (in private, in a linked admin chat, or both), one per
    /// member a day and edited as signs add up, their buttons (Ban, Mute, Fine) and the labels they write,
    /// reports held during a raid, a restart in between, and the notice when a filter rule's trial ends.
    /// Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class DetectionReportScenarios
    {
        private static readonly SignDefinition Reported = SignCatalog.AddForTests("test_reported", 1, "report", report: true);
        private static readonly SignDefinition Scored = SignCatalog.AddForTests("test_scored", 2, "score");

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static string S(HarnessChat chat, string key, params object[] args) =>
            Methods.GetLocaleString(Methods.GetGroupLanguage(chat.ChatId).Doc, key, args);

        private static Message[] Private(HarnessChat chat, HarnessUser user) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == user.Id).ToArray();

        private static Message[] ReportsTo(HarnessChat chat, long to) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == to && m.Text?.StartsWith("🛡") == true).ToArray();

        private static TelegramCall[] Edits(HarnessChat chat, long to) =>
            chat.Telegram.Accepted.Where(c => c.Method == "editMessageText" && c.ChatId == to).ToArray();

        private static InlineKeyboardButton[] Buttons(Message message) =>
            message.ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray() ?? Array.Empty<InlineKeyboardButton>();

        private static InlineKeyboardButton[] Buttons(TelegramCall edit) =>
            ((Telegram.Bot.Requests.EditMessageTextRequest)edit.Request).ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray()
            ?? Array.Empty<InlineKeyboardButton>();

        private static Task GoLive(HarnessChat chat) =>
            chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) } });

        private static async Task Hit(HarnessChat chat, HarnessUser user, Message message, SignDefinition sign)
        {
            await DetectionEngine.HitAsync(chat.ChatId, user.Id, message, new SignHit(sign.Name));
            await chat.SettleAsync();
        }

        private static Task<DetectionPolicy> Stored(HarnessChat chat) =>
            new PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId);

        private sealed class LabelRow
        {
            public string Label { get; set; }
            public string Action { get; set; }
            public string Source { get; set; }
            public long? DecidedBy { get; set; }
            public string Reason { get; set; }
            public long? MessageId { get; set; }
        }

        private static async Task<LabelRow[]> Labels(HarnessChat chat, long userId)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<LabelRow>(@"
SELECT l.label AS Label, l.action AS Action, l.source AS Source, actor.telegram_user_id AS DecidedBy, l.reason AS Reason,
       l.message_id AS MessageId
FROM member_labels l
JOIN telegram_accounts a ON a.account_id = l.account_id
LEFT JOIN telegram_accounts actor ON actor.account_id = l.decided_by_account_id
WHERE a.telegram_user_id = @UserId ORDER BY l.label_id", new { UserId = userId })).ToArray();
        }

        private static async Task<(long Chat, string Decision)[]> Copies(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, string)>("SELECT chat_id, decision FROM report_copies ORDER BY copy_id")).ToArray();
        }

        [TestMethod]
        public async Task ALiveSign_ReportsToEveryAdminWhoStartedTheBot_WithTheButtons()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var notStarted = chat.Add("Quiet admin", MemberState.Administrator);
            chat.Telegram.SetNotStarted(notStarted.Id);
            var member = chat.Add("Spammer", username: "spammer");
            var message = await chat.SendAsync(member, "hello");

            await Hit(chat, member, message, Reported);

            var report = ReportsTo(chat, admin.Id).Single();
            StringAssert.Contains(report.Text, chat.Title);
            StringAssert.Contains(report.Text, "Spammer");
            StringAssert.Contains(report.Text, member.Id.ToString());
            StringAssert.Contains(report.Text, "test_reported");
            Assert.IsFalse(report.Text.Contains("hello"), "never the message's text");
            var buttons = Buttons(report);
            var data = buttons.Where(b => b.CallbackData != null).Select(b => b.CallbackData.Split(':')).ToArray();
            CollectionAssert.AreEqual(new[] { "ban", "mute", "fine" }, data.Select(d => d[4]).ToArray());
            Assert.IsTrue(data.All(d => d[0] == DetectionReports.Trigger && d[1] == chat.ChatId.ToString() && d[2] == member.Id.ToString()));
            Assert.AreEqual(KeyboardButtonStyle.Danger, buttons[0].Style);
            Assert.AreEqual(Methods.GetChatMessageLink(chat.ChatId, message.MessageId.ToString()), buttons.Single(b => b.Url != null).Url);
            Assert.AreEqual(0, ReportsTo(chat, notStarted.Id).Length);
            CollectionAssert.AreEqual(new[] { (admin.Id, (string)null) }, await Copies(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABurstOfReports_ReachesTheAdmin_PacedUnderTelegramsLimits()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var members = Enumerable.Range(0, 15).Select(i => chat.Add($"Member{i}")).ToList();

            foreach (var member in members)
                DetectionReports.Report(chat.ChatId, member.Id, null, Reported.Name, member.ToUser());
            await chat.AdvanceAsync(TimeSpan.FromMinutes(2));

            Assert.AreEqual(members.Count, ReportsTo(chat, admin.Id).Length);
            Assert.AreEqual(0, chat.Telegram.LimitRefusals.Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhileTheGroupOnlyWatches_NothingIsReported()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");

            await Hit(chat, member, message, Reported);

            Assert.AreEqual(0, ReportsTo(chat, admin.Id).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MoreSigns_EditTheOneReport_OnceEveryThirtySeconds()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var first = await chat.SendAsync(member, "one");
            await Hit(chat, member, first, Reported);

            var second = await chat.SendAsync(member, "two");
            await DetectionEngine.HitAsync(chat.ChatId, member.Id, second, new SignHit(Scored.Name));
            await DetectionReports.ReportAsync(chat.ChatId, member.Id, second.MessageId, Reported.Name, null, 3);
            await DetectionReports.ReportAsync(chat.ChatId, member.Id, second.MessageId, Reported.Name, null, 3);
            await chat.SettleAsync();
            Assert.AreEqual(0, Edits(chat, admin.Id).Length, "not before 30 seconds");

            await chat.AdvanceAsync(TimeSpan.FromSeconds(31));

            Assert.AreEqual(1, ReportsTo(chat, admin.Id).Length, "one report a day");
            var edit = Edits(chat, admin.Id).Single();
            StringAssert.Contains(edit.Text, "test_reported ×3");
            StringAssert.Contains(edit.Text, S(chat, "reportScore", 3));
            Assert.AreEqual(Methods.GetChatMessageLink(chat.ChatId, second.MessageId.ToString()), Buttons(edit).Single(b => b.Url != null).Url);

            // A day later it is a new report.
            await chat.AdvanceAsync(TimeSpan.FromHours(24));
            await DetectionReports.ReportAsync(chat.ChatId, member.Id, second.MessageId, Reported.Name, null, 0);
            await chat.SettleAsync();
            Assert.AreEqual(2, ReportsTo(chat, admin.Id).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Ban_FromAReport_BansOnce_LabelsIt_AndEveryCopySaysWho()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("Other admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var outsider = chat.Add("Outsider");
            var message = await chat.SendAsync(member, "hello");
            await Hit(chat, member, message, Reported);
            var mine = ReportsTo(chat, admin.Id).Single();
            var theirs = ReportsTo(chat, other.Id).Single();
            var theirBan = Buttons(theirs).First(b => b.CallbackData != null).CallbackData;

            await chat.PressAsync(outsider, mine, $"{DetectionReports.Trigger}:");
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, member.Id), "only admins can press");

            await chat.PressAsync(admin, mine, $"{DetectionReports.Trigger}:");
            await chat.SettleAsync();

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id));
            var label = (await Labels(chat, member.Id)).Single(l => l.Action == "ban");
            Assert.AreEqual(("bad", "report", admin.Id), (label.Label, label.Source, label.DecidedBy));
            foreach (var to in new[] { admin.Id, other.Id })
            {
                var edit = Edits(chat, to).Last();
                StringAssert.Contains(edit.Text, "Banned by");
                StringAssert.Contains(edit.Text, admin.Id.ToString());
                Assert.IsFalse(Buttons(edit).Any(b => b.CallbackData != null), "decided: no more buttons");
            }
            Assert.IsTrue((await Copies(chat)).All(c => c.Decision == "ban"));

            // The other admin's copy, pressed before it was edited: the first decision stands.
            await chat.PressDataAsync(other, theirs, theirBan);
            var answer = chat.Telegram.Accepted.Last(c => c.Method == "answerCallbackQuery").Text;
            StringAssert.StartsWith(answer, "🔨");
            Assert.AreEqual(1, (await Labels(chat, member.Id)).Count(l => l.Action == "ban"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Fine_ClearsTheScore_AndTheSameSignsLeaveThemAlone_AlsoAfterARestart()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");
            await Hit(chat, member, message, Scored);
            await Hit(chat, member, message, Reported);
            var report = ReportsTo(chat, admin.Id).Single();

            await chat.PressDataAsync(admin, report, Buttons(report).Single(b => b.CallbackData?.EndsWith(":fine") == true).CallbackData);
            await chat.SettleAsync();

            var fine = (await Labels(chat, member.Id)).Single(l => l.Action == "fine");
            Assert.AreEqual(("fine", "report", admin.Id), (fine.Label, fine.Source, fine.DecidedBy));
            StringAssert.Contains(fine.Reason, Reported.Name);
            StringAssert.Contains(fine.Reason, Scored.Name);
            Assert.AreEqual(0, MemberScores.Of(chat.ChatId, member.Id));
            StringAssert.Contains(Edits(chat, admin.Id).Last().Text, "Marked fine by");

            await chat.CrashAndRestartAsync();
            await chat.AdvanceAsync(TimeSpan.FromDays(2));
            Assert.IsNull(await DetectionEngine.HitAsync(chat.ChatId, member.Id, message, new SignHit(Reported.Name)), "fine for this sign");
            var other = SignCatalog.AddForTests("test_other_reported", 1, "report", report: true);
            Assert.IsNotNull(await DetectionEngine.HitAsync(chat.ChatId, member.Id, message, new SignHit(other.Name)));
            await chat.SettleAsync();
            Assert.AreEqual(1, MemberScores.Of(chat.ChatId, member.Id), "the score starts after the Fine");
            Assert.AreEqual(2, ReportsTo(chat, admin.Id).Length, "a new sign is a new report");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AfterARestart_ANewSignEditsTheSameReport_AndItsButtonsStillWork()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");
            await Hit(chat, member, message, Reported);
            await DetectionRecorder.FlushAsync();
            var report = ReportsTo(chat, admin.Id).Single();

            await chat.CrashAndRestartAsync();
            await Hit(chat, member, message, Reported);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(31));
            Assert.AreEqual(1, ReportsTo(chat, admin.Id).Length);
            StringAssert.Contains(Edits(chat, admin.Id).Last().Text, "test_reported ×2");

            await chat.CrashAndRestartAsync();
            await chat.PressDataAsync(admin, report, Buttons(report).Single(b => b.CallbackData?.EndsWith(":mute") == true).CallbackData);
            await chat.SettleAsync();
            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual("report", (await Labels(chat, member.Id)).Single(l => l.Action == "mute").Source);
            AssertNoErrors(chat);
        }

        /// <summary>A menu press, a little after the last, as a person would: the command limiter slows down a burst.</summary>
        private static async Task Tap(HarnessChat chat, HarnessUser admin, Message menu, string data)
        {
            await chat.AdvanceAsync(TimeSpan.FromSeconds(15));
            await chat.PressDataAsync(admin, menu, data);
        }

        [TestMethod]
        public async Task TheReportsMenu_LinksAnAdminChat_OnceAndOnlyForAdminsOfBoth()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var stranger = chat.Add("Member");
            var adminChat = chat.Group(-1009990001, "Admins only");
            chat.Telegram.SetMember(adminChat.ChatId, admin.Id, MemberState.Administrator);
            chat.Telegram.SetMember(adminChat.ChatId, stranger.Id, MemberState.Member);

            await chat.SendAsync(admin, "/menu");
            var menu = Private(chat, admin).Last();
            await Tap(chat, admin, menu, $"reportsmenu:{chat.ChatId}");
            StringAssert.Contains(Edits(chat, admin.Id).Last().Text, S(chat, "reportsTo_admins"));
            await Tap(chat, admin, menu, $"reportslink:{chat.ChatId}");
            var url = Buttons(Edits(chat, admin.Id).Last()).Single(b => b.Url != null).Url;
            StringAssert.StartsWith(url, $"https://t.me/{HarnessChat.BotUser.Username}?startgroup={Enforcer5.Commands.AdminChatStart}");
            var start = "/start " + url.Substring(url.IndexOf("startgroup=", StringComparison.Ordinal) + "startgroup=".Length);

            await adminChat.SendAsync(stranger, start);
            Assert.AreEqual(S(chat, "adminChatNotAdmin"), adminChat.Replies.Last());
            Assert.IsNull((await Stored(chat)).Engine.AdminChatId);
            await adminChat.SendAsync(admin, start);
            Assert.AreEqual(S(chat, "adminChatLinked", chat.Title), adminChat.Replies.Last());
            await adminChat.SendAsync(admin, start);
            Assert.AreEqual(S(chat, "adminChatLinkExpired"), adminChat.Replies.Last(), "once only");
            var engine = (await Stored(chat)).Engine;
            Assert.AreEqual((adminChat.ChatId, EngineSettings.ToAdminChat), (engine.AdminChatId, engine.ReportTo));

            // The admin chat has had three messages in the last seconds: the report waits for its turn.
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));
            var member = chat.Add("Spammer");
            var message = await chat.SendAsync(member, "hello");
            await Hit(chat, member, message, Reported);
            Assert.AreEqual(1, ReportsTo(chat, adminChat.ChatId).Length);
            Assert.AreEqual(0, ReportsTo(chat, admin.Id).Length, "the admin chat only");

            // Both, then the admin turns theirs off.
            await Tap(chat, admin, menu, $"reportsto:{chat.ChatId}");
            Assert.AreEqual(EngineSettings.ToBoth, (await Stored(chat)).Engine.ReportTo);
            await Tap(chat, admin, menu, $"reportsme:{chat.ChatId}");
            var next = chat.Add("Next");
            await Hit(chat, next, await chat.SendAsync(next, "hi"), Reported);
            Assert.AreEqual(2, ReportsTo(chat, adminChat.ChatId).Length);
            Assert.AreEqual(0, ReportsTo(chat, admin.Id).Length, "opted out");

            await Tap(chat, admin, menu, $"reportsunlink:{chat.ChatId}");
            Assert.IsNull((await Stored(chat)).Engine.AdminChatId);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task DuringARaid_ReportsAreHeld_AndComeAsOneWhenItEnds()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            Enforcer5.Raids.RaidGuard.Activate(chat.ChatId, Enforcer5.Raids.RaidActions.Quarantine, "command", admin.Id);
            await chat.SettleAsync();
            var raiders = Enumerable.Range(0, 3).Select(i => chat.Add($"Raider{i}")).ToList();
            foreach (var raider in raiders)
                await DetectionReports.ReportAsync(chat.ChatId, raider.Id, null, Reported.Name, raider.ToUser(), 0);
            await chat.SettleAsync();
            Assert.AreEqual(0, ReportsTo(chat, admin.Id).Length);

            Enforcer5.Raids.RaidGuard.End(chat.ChatId, admin.Id);
            await chat.SettleAsync();

            var summary = ReportsTo(chat, admin.Id).Single();
            foreach (var raider in raiders) StringAssert.Contains(summary.Text, raider.FirstName);
            Assert.IsNull(summary.ReplyMarkup);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AFilterRuleSetToReport_ReportsTheMember()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/filter add text report free +crypto");
            var rule = (await Stored(chat)).Filters.OwnRules.Single();
            await chat.SendAsync(admin, $"/filter live {rule.Id}");
            var member = chat.Add("Member");

            await chat.SendAsync(member, "get free crypto");

            var report = ReportsTo(chat, admin.Id).Single();
            StringAssert.Contains(report.Text, S(chat, "reportSignRule", rule.Id));
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, member.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhenATrialEnds_TheAdminsAreAskedOnce_AndExtendAsksAgainLater()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/filter add text ban free +crypto");
            var rule = (await Stored(chat)).Filters.OwnRules.Single();
            var member = chat.Add("Member");
            await chat.SendAsync(member, "get free crypto");
            await DetectionRecorder.FlushAsync();

            await FilterTrials.CheckOnceAsync();
            Assert.AreEqual(0, Private(chat, admin).Count(m => m.Text?.Contains("🧪") == true), "still on trial");

            await chat.AdvanceAsync(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
            await FilterTrials.CheckOnceAsync();
            await FilterTrials.CheckOnceAsync();
            await chat.SettleAsync();

            var notice = Private(chat, admin).Single(m => m.Text?.Contains("🧪") == true);
            StringAssert.Contains(notice.Text, $"#{rule.Id}");
            StringAssert.Contains(notice.Text, "free +crypto");
            StringAssert.Contains(notice.Text, "matched 1 times");
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, member.Id), "still recording only");

            await chat.PressAsync(admin, notice, $"{FilterTrials.Trigger}:");
            // The first button is Make live; Extend is pressed by data to test it.
            var live = (await Stored(chat)).Filters.OwnRules.Single();
            Assert.AreEqual(FilterRule.Live, live.Mode);
            StringAssert.Contains(Edits(chat, admin.Id).Last().Text, "Made live by");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Extend_StartsANewTrial_WhoseEndIsAskedAboutAgain()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/filter add text ban free +crypto");
            await chat.AdvanceAsync(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
            await FilterTrials.CheckOnceAsync();
            await chat.SettleAsync();
            var notice = Private(chat, admin).Single(m => m.Text?.Contains("🧪") == true);

            await chat.PressDataAsync(admin, notice, Buttons(notice).Single(b => b.CallbackData?.EndsWith(":extend") == true).CallbackData);

            var rule = (await Stored(chat)).Filters.OwnRules.Single();
            Assert.AreEqual(FilterRule.Trial, rule.Mode);
            Assert.IsTrue(rule.TrialUntil > chat.Clock.GetUtcNow().AddDays(6));
            await chat.AdvanceAsync(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
            await FilterTrials.CheckOnceAsync();
            await chat.SettleAsync();
            Assert.AreEqual(2, Private(chat, admin).Count(m => m.Text?.Contains("🧪") == true));

            var second = Private(chat, admin).Last(m => m.Text?.Contains("🧪") == true);
            await chat.PressDataAsync(admin, second, Buttons(second).Single(b => b.CallbackData?.EndsWith(":delete") == true).CallbackData);
            Assert.AreEqual(0, (await Stored(chat)).Filters.OwnRules.Count());
            StringAssert.Contains(Edits(chat, admin.Id).Last().Text, "Deleted by");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task IgnoreOnALinkReport_IsAFineLabel_FromTheRaidButtons()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");
            var report = await chat.SendAsync(admin, "x");

            await chat.PressDataAsync(admin, report, $"raidlink:{chat.ChatId}:ignore:{member.Id}:{message.MessageId}");
            await chat.SettleAsync();

            var label = (await Labels(chat, member.Id)).Single();
            Assert.AreEqual(("fine", "fine", "raid", admin.Id), (label.Label, label.Action, label.Source, label.DecidedBy));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task DeleteOnAnAdminReport_IsADeleteLabel()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var reporter = chat.Add("Reporter");
            var message = await chat.SendAsync(member, "spam");
            await chat.SendAsync(reporter, "/report", replyTo: message);
            var report = Private(chat, admin).Last(m => m.ReplyMarkup != null);

            await chat.PressAsync(admin, report, "delflag:");
            await chat.SettleAsync();

            var label = (await Labels(chat, member.Id)).Single();
            Assert.AreEqual(("bad", "delete", "report", admin.Id, (long?)message.MessageId),
                (label.Label, label.Action, label.Source, label.DecidedBy, label.MessageId));
            AssertNoErrors(chat);
        }
    }
}
