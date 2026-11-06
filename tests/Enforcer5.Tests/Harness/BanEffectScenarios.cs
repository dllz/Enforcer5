using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// What the ban actions own since step B of the action seam (<c>docs/action-seam.md</c>, section 8): the
    /// tempban schedule, the ban list /status reads and the ban record, the same from every path. Needs the
    /// test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class BanEffectScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static int Scheduled(HarnessChat chat) => chat.TempbansAsync().GetAwaiter().GetResult().Length;

        private static async Task<(HarnessUser Admin, HarnessUser Member, Message Post)> TempbannedAsync(HarnessChat chat)
        {
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "buy now");
            await chat.SendAsync(admin, "/tempban 2 h", replyTo: post);
            Assert.AreEqual(member.Id, (await chat.TempbansAsync()).Single().UserId);
            return (admin, member, post);
        }

        /// <summary>Banned for good: the tempban's end must not let them back in.</summary>
        private static async Task AssertStaysBannedAsync(HarnessChat chat, HarnessUser member)
        {
            Assert.AreEqual(0, Scheduled(chat), "the tempban is out of the schedule");
            await chat.ExpireTempbansAsync();
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id));
        }

        [TestMethod]
        public async Task TheReportBanButton_EndsARunningTempban()
        {
            // Issue #24: the button compared "{chat}:{user}" with the stored "{chat}:{user}:{name}:{group}".
            await using var chat = await HarnessChat.StartAsync();
            var (admin, member, post) = await TempbannedAsync(chat);
            chat.Telegram.SetMember(chat.ChatId, member.Id, MemberState.Member); // an admin let them back early
            var reporter = chat.Add("Reporter");
            var spam = await chat.SendAsync(member, "buy now again");
            await chat.SendAsync(reporter, "@admin spam", replyTo: spam);

            await chat.PressAsync(admin, chat.Telegram.LastMessageTo(admin.Id), "banflag:");

            await AssertStaysBannedAsync(chat, member);
            Assert.IsNotNull(chat.Hashes[$"chat:{chat.ChatId}:bannedlist:{member.Id}", "why"], "the reason /status shows");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheUserMenusBanButton_EndsARunningTempban()
        {
            await using var chat = await HarnessChat.StartAsync();
            var (admin, member, _) = await TempbannedAsync(chat);

            await chat.SendAsync(admin, $"/user {member.Id}");
            await chat.PressAsync(admin, chat.Telegram.SentMessages.Last(m => m.ReplyMarkup?.InlineKeyboard
                .SelectMany(r => r).Any(b => b.CallbackData?.StartsWith("userbuttonbanuser:") == true) == true), "userbuttonbanuser:");

            await AssertStaysBannedAsync(chat, member);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABan_ByTheBotsOwnCheck_EndsARunningTempbanToo()
        {
            await using var chat = await HarnessChat.StartAsync();
            // Before the first message: the settings are read once into the group's policy.
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no"; // "yes" switches it off
            chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = "5";
            chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = "ban";
            var (_, member, _) = await TempbannedAsync(chat);
            chat.Telegram.SetMember(chat.ChatId, member.Id, MemberState.Member);
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1)); // the tempban command's messages out of the flood window

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Text(member, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id), string.Join(" | ", chat.Replies) + " / " + string.Join(",", chat.Telegram.Accepted.Select(c => c.Method)));
            await AssertStaysBannedAsync(chat, member);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ASecondTempban_ReplacesTheFirst()
        {
            await using var chat = await HarnessChat.StartAsync();
            var (admin, member, post) = await TempbannedAsync(chat);
            chat.Telegram.SetMember(chat.ChatId, member.Id, MemberState.Member);

            await chat.SendAsync(admin, "/tempban 3 d", replyTo: post);

            var entry = (await chat.TempbansAsync()).Single();
            Assert.AreEqual(member.Id, entry.UserId);
            Assert.IsTrue(Math.Abs((chat.Clock.UtcNow.AddDays(3) - entry.Until).TotalSeconds) < 5, "the second one's end");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Unban_EndsTheTempban_AndClearsTheBanReason()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "buy now");
            await chat.SendAsync(admin, "/ban selling", replyTo: post);
            Assert.AreEqual("selling", chat.Hashes[$"chat:{chat.ChatId}:bannedlist:{member.Id}", "why"]);
            Assert.AreEqual("1", chat.Hashes[$"ban:{member.Id}", "ban"], "counted in their ban record");

            await chat.SendAsync(admin, $"/unban {member.Id}");

            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.IsNull(chat.Hashes[$"chat:{chat.ChatId}:bannedlist:{member.Id}", "why"]);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task UnbanAfterATempban_TakesItOutOfTheSchedule()
        {
            await using var chat = await HarnessChat.StartAsync();
            var (admin, member, _) = await TempbannedAsync(chat);

            await chat.SendAsync(admin, $"/unban {member.Id}");

            Assert.AreEqual(0, Scheduled(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ComingBackEarly_TakesThemOffTheTempbanList()
        {
            await using var chat = await HarnessChat.StartAsync();
            var (admin, member, _) = await TempbannedAsync(chat);
            chat.Telegram.SetMember(chat.ChatId, member.Id, MemberState.Left);

            await chat.JoinAsync(member);

            Assert.AreEqual(0, Scheduled(chat), "/tempbans no longer lists them");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ARefusedBan_LeavesTheTempbanAlone()
        {
            await using var chat = await HarnessChat.StartAsync();
            var (admin, member, post) = await TempbannedAsync(chat);
            chat.Telegram.SetBotCanRestrict(chat.ChatId, false);

            await chat.SendAsync(admin, "/ban", replyTo: post);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(1, Scheduled(chat), "nothing happened, so nothing is forgotten");
        }
    
        [TestMethod]
        public async Task TheInlineTempbanQuery_ShowsTheMembersOwnTempban_AndItsRealEnd()
        {
            await using var chat = await HarnessChat.StartAsync(title: "Cats & Dogs");
            chat.Hashes[$"chat:{chat.ChatId}:details", "name"] = "Cats & Dogs";
            var (_, member, _) = await TempbannedAsync(chat);
            var other = chat.Add("Other");

            var answer = await chat.InlineAsync(member, "tempban");

            Assert.IsNotNull(answer, "answered");
            var result = (Telegram.Bot.Types.InlineQueryResults.InlineQueryResultArticle)answer.Results.Single();
            Assert.AreEqual("Cats & Dogs", result.Description, "plain text, escaped nowhere");
            var text = ((Telegram.Bot.Types.InlineQueryResults.InputTextMessageContent)result.InputMessageContent).MessageText;
            StringAssert.Contains(text, "<b>Cats &amp; Dogs</b>");
            var ends = DateTimeOffset.FromUnixTimeSeconds((await chat.TempbanEndAsync(member)).Value).UtcDateTime;
            StringAssert.Contains(text, $"{ends:HH:mm:ss dd-MM-yyyy} UTC", "when it really ends, no two hours off");

            var nothing = await chat.InlineAsync(other, "tempban");
            Assert.AreEqual("Nothing Found", ((Telegram.Bot.Types.InlineQueryResults.InlineQueryResultArticle)nothing.Results.Single()).Title);
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task ATempbanWhilePostgresIsDown_IsQueued_AndLiftedOnceItIsBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "buy now");
            var store = (Helpers.TempbanStore)Data.Repositories.Tempbans;

            Enforcer5.Storage.PostgresStorage.UseForTests(null);
            try
            {
                await chat.SendAsync(admin, "/tempban 2 h", replyTo: post);
                Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id), "the tempban itself does not wait");
                Assert.AreEqual(1, store.Queued);
            }
            finally
            {
                Enforcer5.Storage.PostgresStorage.UseForTests(chat.Database);
            }
            Assert.AreEqual(0, Scheduled(chat), "not written yet");

            await store.DrainAsync();
            Assert.AreEqual(1, Scheduled(chat));
            await chat.ExpireTempbansAsync();

            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual(0, Scheduled(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Tempban_WithAShortUnit_LastsThatLong()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var a = chat.Add("A");
            var b = chat.Add("B");

            await chat.SendAsync(admin, "/tempban 2 h", replyTo: await chat.SendAsync(a, "x"));
            await chat.SendAsync(admin, "/tempban 3d", replyTo: await chat.SendAsync(b, "y"));

            long Ends(HarnessUser u) => chat.TempbanEndAsync(u).GetAwaiter().GetResult().Value;
            var now = new DateTimeOffset(chat.Clock.UtcNow).ToUnixTimeSeconds();
            Assert.AreEqual(2 * 3600, Ends(a) - now, 5, "two hours, not two minutes");
            Assert.AreEqual(3 * 86400, Ends(b) - now, 5, "three days");

            var c = chat.Add("C");
            await chat.SendAsync(admin, $"/tempban {c.Id} 90m");
            Assert.AreEqual(90 * 60, Ends(c) - now, 5, "by id, as /tempmute reads it");

            var d = chat.Add("D");
            await chat.SendAsync(admin, "/tempban 2 fortnights", replyTo: await chat.SendAsync(d, "z"));
            Assert.AreEqual(Helpers.Methods.GetLocaleString(Helpers.Methods.GetGroupLanguage(chat.ChatId).Doc, "invalidDuration"),
                chat.Replies.Last(), "a unit it does not know is refused, not read as minutes");
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, d.Id));
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }
}
}
