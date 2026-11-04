using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// People's names the bot shows are mentions, linking to them (Daniel, 2026-11-04): the member it is about and
    /// the admin who acted, in the group, the log channel and the admins' reports. Names are escaped once, and no
    /// mark reaches Telegram. Needs the test database, like every harness scenario.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class MentionScenarios
    {
        private const long LogChannel = -1009999000077;

        private static string Link(long id, string html) => $"<a href=\"tg://user?id={id}\">{html}</a>";

        private static void AssertClean(HarnessChat chat)
        {
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));
            Assert.IsFalse(chat.Telegram.Calls.Any(c => c.Text != null && Helpers.Ids.HasMarks(c.Text)), "no mark reaches Telegram");
        }

        [TestMethod]
        public async Task ABan_MentionsTheMemberAndTheAdmin_InTheGroupAndTheLog_EscapedOnce()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet("logChatGroups", chat.ChatId.ToString());
            chat.Hashes[$"chat:{chat.ChatId}:settings", "logchat"] = LogChannel.ToString();
            var admin = chat.Add("Ann & Co", MemberState.Administrator);
            var member = chat.Add("<Spam>");
            var post = await chat.SendAsync(member, "buy followers");

            await chat.SendAsync(admin, "/ban", replyTo: post);

            var reply = chat.Replies.Last();
            StringAssert.Contains(reply, $"{Link(member.Id, "&lt;Spam&gt;")} (<code>{member.Id}</code>)");
            StringAssert.Contains(reply, $"{Link(admin.Id, "Ann &amp; Co")} (<code>{admin.Id}</code>)");
            var log = chat.Telegram.SentMessages.Last(m => m.Chat.Id == LogChannel).Text;
            StringAssert.Contains(log, $"{Link(admin.Id, "Ann &amp; Co")} (<code>{admin.Id}</code>)");
            StringAssert.Contains(log, $"{Link(member.Id, "&lt;Spam&gt;")} (<code>{member.Id}</code>)");
            AssertClean(chat);
        }

        [TestMethod]
        public async Task TheAdminList_MentionsTheAdmins_AndEscapesTheElevatedOnes()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Boss", MemberState.Administrator);
            chat.Keys.AddToSet($"chat:{chat.ChatId}:mod", "4242");
            chat.Hashes["user:4242", "name"] = "<b>Mod</b>";

            await chat.SendAsync(admin, "/adminlist");

            var text = chat.Telegram.Calls.Last(c => c.Text != null && c.Text.Contains("Mod")).Text;
            StringAssert.Contains(text, Link(admin.Id, "Boss"));
            StringAssert.Contains(text, $"{Link(4242, "&lt;b&gt;Mod&lt;/b&gt;")} (<code>4242</code>)");
            AssertClean(chat);
        }

        [TestMethod]
        public async Task AReport_MentionsTheReporter_ToTheAdmins()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var reporter = chat.Add("Rita", username: "rita");
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "spam");

            await chat.SendAsync(reporter, "@admin", replyTo: post);

            var report = chat.Telegram.SentMessages.LastOrDefault(m => m.Chat.Id == admin.Id)?.Text;
            Assert.IsNotNull(report, "the admin got the report");
            StringAssert.Contains(report, $"{Link(reporter.Id, "Rita")} (@rita)");
            AssertClean(chat);
        }
    
        [TestMethod]
        public async Task TheBotsOwnLogLine_ShowsANameOnce_NotEscapedTwice()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet("logChatGroups", chat.ChatId.ToString());
            chat.Hashes[$"chat:{chat.ChatId}:settings", "logchat"] = LogChannel.ToString();
            var member = chat.Add("A&B");
            var lang = Helpers.Methods.GetGroupLanguage(chat.ChatId).Doc;
            // What the bot told the group (as the filters, Detection and BotGuard build it), logged as its own action.
            var told = Helpers.Methods.GetLocaleString(lang, "bannedByFilter", $"{Helpers.Ids.Mention(member.Id, "A&B")}, {Helpers.Ids.Mark(member.Id)}", "x<y");

            Service.LogBotAction(chat.ChatId, told, member.Id);
            await chat.AdvanceAsync(System.TimeSpan.FromSeconds(5));

            var line = chat.Telegram.SentMessages.Last(m => m.Chat.Id == LogChannel).Text;
            StringAssert.Contains(line, "A&amp;B", line);
            StringAssert.Contains(line, "x&lt;y", line);
            Assert.IsFalse(line.Contains("&amp;amp;") || line.Contains("&amp;lt;"), line);
            AssertClean(chat);
        }
}
}
