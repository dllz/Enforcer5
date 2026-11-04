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

        private static int Scheduled(HarnessChat chat) => chat.Hashes.All(HarnessChat.TempbanSchedule).Count;

#if PREMIUM
        private const string ChatList = "tempbannedPremium";
#else
        private const string ChatList = "tempbanned";
#endif

        private static async Task<(HarnessUser Admin, HarnessUser Member, Message Post)> TempbannedAsync(HarnessChat chat)
        {
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "buy now");
            await chat.SendAsync(admin, "/tempban 2 h", replyTo: post);
            Assert.AreEqual(1, Scheduled(chat));
            Assert.IsTrue(chat.Keys.Members($"chat:{chat.ChatId}:{ChatList}").Contains(member.Id.ToString()));
            return (admin, member, post);
        }

        /// <summary>Banned for good: the tempban's end must not let them back in.</summary>
        private static async Task AssertStaysBannedAsync(HarnessChat chat, HarnessUser member)
        {
            Assert.AreEqual(0, Scheduled(chat), "the tempban is out of the schedule");
            Assert.IsFalse(chat.Keys.Members($"chat:{chat.ChatId}:{ChatList}").Contains(member.Id.ToString()));
            Assert.IsNull(chat.Keys[$"chat:{chat.ChatId}:tempbanned:{member.Id}"]);
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

            var entry = chat.Hashes.All(HarnessChat.TempbanSchedule).Single();
            StringAssert.StartsWith(entry.Value, $"{chat.ChatId}:{member.Id}:");
            Assert.AreEqual(chat.Keys[$"chat:{chat.ChatId}:tempbanned:{member.Id}"], entry.Key);
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
            Assert.IsFalse(chat.Keys.Members($"chat:{chat.ChatId}:{ChatList}").Contains(member.Id.ToString()));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ComingBackEarly_TakesThemOffTheTempbanList()
        {
            await using var chat = await HarnessChat.StartAsync();
            var (admin, member, _) = await TempbannedAsync(chat);
            chat.Telegram.SetMember(chat.ChatId, member.Id, MemberState.Left);

            await chat.JoinAsync(member);

            Assert.AreEqual(0, Scheduled(chat));
            Assert.IsFalse(chat.Keys.Members($"chat:{chat.ChatId}:{ChatList}").Contains(member.Id.ToString()), "/tempbans no longer lists them");
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
            var ends = DateTimeOffset.FromUnixTimeSeconds(long.Parse(chat.Keys[$"chat:{chat.ChatId}:tempbanned:{member.Id}"])).UtcDateTime;
            StringAssert.Contains(text, $"{ends:HH:mm:ss dd-MM-yyyy} UTC", "when it really ends, no two hours off");

            var nothing = await chat.InlineAsync(other, "tempban");
            Assert.AreEqual("Nothing Found", ((Telegram.Bot.Types.InlineQueryResults.InlineQueryResultArticle)nothing.Results.Single()).Title);
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
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

            long Ends(HarnessUser u) => long.Parse(chat.Keys[$"chat:{chat.ChatId}:tempbanned:{u.Id}"]);
            var now = new DateTimeOffset(chat.Clock.UtcNow).ToUnixTimeSeconds();
            Assert.AreEqual(2 * 3600, Ends(a) - now, 5, "two hours, not two minutes");
            Assert.AreEqual(3 * 86400, Ends(b) - now, 5, "three days");
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }
}
}
