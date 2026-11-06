using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// What a report shows (Daniel, 2026-11-06): a game's command to a trusted bot is no campaign, a name nobody can place is not
    /// an outside chat, the links and names a sign saw are named also after a restart, and Show messages sends the text of every
    /// message the report is about. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class FlaggedTextScenarios
    {
        private static readonly SignDefinition Reported = SignCatalog.AddForTests("test_flagged_reported", 1, "report", report: true);

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static Task ReportAtOne(HarnessChat chat) => chat.ChangeSettingsAsync(p => p with
        {
            Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1), ReportAt = 1 }
        });

        private static async Task<string[]> SignsOf(HarnessChat chat, HarnessUser user)
        {
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>(@"
SELECT d.sign FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE a.telegram_user_id = @Id ORDER BY d.sign", new { user.Id })).ToArray();
        }

        private static Message[] ReportsTo(HarnessChat chat, long to) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == to && m.Text?.StartsWith("🛡") == true).ToArray();

        private static InlineKeyboardButton[] Buttons(Message message) =>
            message.ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray() ?? Array.Empty<InlineKeyboardButton>();

        /// <summary>A text with its command, links and @mentions marked, as Telegram sends them.</summary>
        private static Message Marked(HarnessChat chat, HarnessUser from, string text)
        {
            var message = chat.Text(from, text);
            message.Entities = Regex.Matches(text, @"^/\w+(@\w+)?").Select(m => new MessageEntity { Type = MessageEntityType.BotCommand, Offset = m.Index, Length = m.Length })
                .Concat(Regex.Matches(text, @"(?<!/\w*)@\w+").Select(m => new MessageEntity { Type = MessageEntityType.Mention, Offset = m.Index, Length = m.Length }))
                .Concat(Regex.Matches(text, @"(https?://|t\.me/)\S+").Select(m => new MessageEntity { Type = MessageEntityType.Url, Offset = m.Index, Length = m.Length }))
                .ToArray();
            return message;
        }

        /// <summary>The private messages Show messages sent to <paramref name="to"/>, and whether each was protected.</summary>
        private static (string Text, bool Protected)[] Shown(HarnessChat chat, long to) =>
            chat.Telegram.Accepted.Where(c => c.Method == "sendMessage" && c.ChatId == to)
                .Select(c => (Telegram.Bot.Requests.SendMessageRequest)c.Request)
                .Where(r => r.Text.StartsWith("📝"))
                .Select(r => (r.Text, r.ProtectContent))
                .ToArray();

        [TestMethod]
        public async Task TheSameCommand_ToTheGamesBot_IsNoCampaign_ToABotNobodyTrustsItIs()
        {
            await using var chat = await HarnessChat.StartAsync();
            var first = chat.Add("First");
            var second = chat.Add("Second");

            await chat.PostAsync(Marked(chat, first, "/startmighty@blackwerewolfbot"));
            await chat.PostAsync(Marked(chat, second, "/startmighty@blackwerewolfbot"));
            Assert.AreEqual(0, (await SignsOf(chat, first)).Length);
            Assert.AreEqual(0, (await SignsOf(chat, second)).Length);

            await chat.PostAsync(Marked(chat, first, "/claimreward@freecryptodropsbot"));
            await chat.PostAsync(Marked(chat, second, "/claimreward@freecryptodropsbot"));
            CollectionAssert.AreEqual(new[] { "same_as_newcomers" }, await SignsOf(chat, first));
            CollectionAssert.AreEqual(new[] { "same_as_newcomers" }, await SignsOf(chat, second));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ACommandToABotTheAdminsTrusted_IsNoCampaign_OnceTheyUntrustItItIs()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var first = chat.Add("First");
            var second = chat.Add("Second");
            const long quizBot = 555000111;
            chat.Hashes["bot:usernames", "@groupquizzesbot"] = quizBot.ToString();
            await BotGuard.TrustAsync(chat.ChatId, quizBot, admin.Id);

            await chat.PostAsync(Marked(chat, first, "/startquizgame@groupquizzesbot"));
            await chat.PostAsync(Marked(chat, second, "/startquizgame@groupquizzesbot"));
            Assert.AreEqual(0, (await SignsOf(chat, second)).Length);

            await BotGuard.UntrustAsync(chat.ChatId, quizBot, admin.Id);
            await chat.PostAsync(Marked(chat, first, "/startquizgame@groupquizzesbot"));
            await chat.PostAsync(Marked(chat, second, "/startquizgame@groupquizzesbot"));
            CollectionAssert.AreEqual(new[] { "same_as_newcomers" }, await SignsOf(chat, first));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ANameNobodyCanPlace_IsNotCounted_ButTheReportNamesIt_ABotElsewhereIsCounted()
        {
            // A player tagged by a username they took since they last posted: Telegram does not show bots who has it.
            await using var chat = await HarnessChat.StartAsync(username: "harnessgroup");
            await ReportAtOne(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.AddPublicChat("cryptogems", -1007777);
            var newcomer = chat.Add("Newcomer");
            var other = chat.Add("Other");

            await chat.PostAsync(Marked(chat, newcomer, "good game @ghostplayer"));
            Assert.AreEqual(0, (await SignsOf(chat, newcomer)).Length, string.Join(",", await SignsOf(chat, newcomer)));

            await chat.PostAsync(Marked(chat, newcomer, "join @cryptogems with @ghostplayer"));
            var report = ReportsTo(chat, admin.Id).Single();
            StringAssert.Contains(report.Text, "mention of a chat or person outside the group</a> (@cryptogems; not found, not counted: @ghostplayer)");

            await chat.PostAsync(Marked(chat, other, "free coins from @spamdropsbot"));
            CollectionAssert.AreEqual(new[] { "mention_outside" }, await SignsOf(chat, other));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheReport_NamesTheLinks_HiddenOnesToo_AlsoAfterARestart()
        {
            await using var chat = await HarnessChat.StartAsync(username: "harnessgroup");
            await ReportAtOne(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var newcomer = chat.Add("Newcomer");
            await chat.SendAsync(newcomer, "hi");
            var message = Marked(chat, newcomer, "see t.me/othergroup and click here");
            message.Entities = message.Entities.Append(new MessageEntity
            {
                Type = MessageEntityType.TextLink, Offset = message.Text.IndexOf("here", StringComparison.Ordinal), Length = 4,
                Url = "https://evil.example/x"
            }).ToArray();

            await chat.PostAsync(message);

            var report = ReportsTo(chat, admin.Id).Single();
            StringAssert.Contains(report.Text, "link to another chat or website</a> (t.me/othergroup, https://evil.example/x)");

            // After a restart the report is read back from the database when a later sign edits it: the links are still named.
            await DetectionRecorder.FlushAsync();
            await chat.CrashAndRestartAsync();
            await DetectionEngine.HitAsync(chat.ChatId, newcomer.Id, message, new SignHit(Reported.Name));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(31));
            var edit = chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == admin.Id);
            StringAssert.Contains(edit.Text, "link to another chat or website</a> (t.me/othergroup, https://evil.example/x)");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ShowMessages_SendsWhatEachNewcomerPosted_ToTheAdminAlone_Protected_BeforeAndAfterARestart()
        {
            await using var chat = await HarnessChat.StartAsync();
            await ReportAtOne(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var first = chat.Add("First");
            var second = chat.Add("Second");
            const string spam = "Earn five hundred dollars a day from home, ask me how";

            await chat.SendAsync(first, spam);
            await chat.AdvanceAsync(TimeSpan.FromHours(1));
            await chat.SendAsync(second, spam + "!!");

            // The first one's report: their message matched later, so it was not passed with the sign.
            var aboutFirst = ReportsTo(chat, admin.Id).Single(m => m.Text.Contains(first.Id.ToString()));
            var show = Buttons(aboutFirst).Single(b => b.CallbackData?.EndsWith(":texts") == true);
            await chat.PressDataAsync(admin, aboutFirst, show.CallbackData);
            var shown = Shown(chat, admin.Id).Single();
            StringAssert.Contains(shown.Text, spam);
            StringAssert.Contains(shown.Text, "same message as other new members");
            Assert.IsTrue(shown.Protected);
            Assert.IsFalse(chat.Telegram.Accepted.Any(c => c.Method == "sendMessage" && c.ChatId == chat.ChatId && c.Text?.Contains(spam) == true),
                "never in the group");

            await DetectionRecorder.FlushAsync();
            await chat.CrashAndRestartAsync();
            var aboutSecond = ReportsTo(chat, admin.Id).Single(m => m.Text.Contains(second.Id.ToString()));
            await chat.PressDataAsync(admin, aboutSecond, Buttons(aboutSecond).Single(b => b.CallbackData?.EndsWith(":texts") == true).CallbackData);
            StringAssert.Contains(Shown(chat, admin.Id).Last().Text, spam + "!!");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Retention_KeepsTheTextOfReportedMessages_ClearsTheRest_AndAllPastTheGroupsChoice()
        {
            await using var chat = await HarnessChat.StartAsync();
            await chat.ChangeSettingsAsync(p => p with
            {
                Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1), ReportAt = 3, RetentionDays = 90 }
            });
            chat.Add("Admin", MemberState.Administrator);
            var quiet = chat.Add("Quiet");
            var reported = chat.Add("Reported");
            await chat.SendAsync(quiet, "hi");
            await chat.SendAsync(reported, "hi");
            chat.Telegram.AddPublicChat("cryptogems", -1007777);
            await chat.PostAsync(Marked(chat, quiet, "see https://one.example"));
            await chat.PostAsync(Marked(chat, reported, "see https://two.example and @cryptogems"));
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            async Task<int> Texts(HarnessUser user) => await connection.ExecuteScalarAsync<int>(@"
SELECT count(*) FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE a.telegram_user_id = @Id AND d.message_text IS NOT NULL", new { user.Id });
            Assert.IsTrue(await Texts(quiet) > 0 && await Texts(reported) > 0);

            await chat.AdvanceAsync(TimeSpan.FromDays(3));
            await DetectionData.NightlyAsync(force: true);
            Assert.AreEqual(0, await Texts(quiet), "no report was about it");
            Assert.IsTrue(await Texts(reported) > 0, "a report can still show it");

            await connection.ExecuteAsync("UPDATE detections SET detected_at = detected_at - interval '100 days'");
            await DetectionData.NightlyAsync(force: true);
            Assert.AreEqual(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM detections WHERE message_text IS NOT NULL OR detail IS NOT NULL"));
            AssertNoErrors(chat);
        }
    }
}
