using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The action seam, step E (<c>docs/action-seam.md</c>): an action writes its own log channel line, in the
    /// wording the callers used, once and only when it went through. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class ActionLogScenarios
    {
        private const long LogChannel = -1006666;

        private static async Task<HarnessChat> WithLogChannelAsync()
        {
            var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet("logChatGroups", chat.ChatId.ToString());
            chat.Hashes[$"chat:{chat.ChatId}:settings", "logchat"] = LogChannel.ToString();
            return chat;
        }

        /// <summary>The log channel's posts. Each scenario's lines come apart in time, so the queue posts each alone.</summary>
        private static async Task<string[]> LogsAsync(HarnessChat chat)
        {
            await chat.AdvanceAsync(System.TimeSpan.FromSeconds(5));
            return chat.Telegram.SentMessages.Where(m => m.Chat.Id == LogChannel).Select(m => m.Text).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task AnAdminsBan_IsLoggedOnce_AsTyped()
        {
            await using var chat = await WithLogChannelAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "spam");

            await chat.SendAsync(admin, "/ban selling followers", replyTo: post);

            var line = (await LogsAsync(chat)).Single();
            StringAssert.Contains(line, "/ban selling followers");
            StringAssert.Contains(line, $"Member (<code>{member.Id}</code>)");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AWarnThatBans_IsOneLine_TheCommand()
        {
            await using var chat = await WithLogChannelAsync();
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "max"] = "1";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "type"] = "ban";
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/warn", replyTo: post);

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id));
            StringAssert.Contains((await LogsAsync(chat)).Single(), "/warn");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AWarnOfAnAdmin_DidNothing_AndIsNotLogged()
        {
            await using var chat = await WithLogChannelAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("Other admin", MemberState.Administrator);
            var post = await chat.SendAsync(other, "hello");

            await chat.SendAsync(admin, "/warn", replyTo: post);

            Assert.AreEqual(0, (await LogsAsync(chat)).Length);
        }

        [TestMethod]
        public async Task SlashD_IsLoggedForSomeoneElsesPost_NotForOnesOwn()
        {
            await using var chat = await WithLogChannelAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var own = await chat.SendAsync(admin, "typo");
            await chat.SendAsync(admin, "/d", replyTo: own);
            Assert.AreEqual(0, (await LogsAsync(chat)).Length, "one's own post");

            var post = await chat.SendAsync(member, "spam");
            await chat.SendAsync(admin, "/d", replyTo: post);

            var line = (await LogsAsync(chat)).Single();
            StringAssert.Contains(line, "/d");
            StringAssert.Contains(line, member.Id.ToString());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheBotsKick_LogsWhatItToldTheGroup_Once()
        {
            await using var chat = await WithLogChannelAsync();
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlength"] = "100";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlines"] = "50";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "action"] = "kick";
            chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            await chat.SendAsync(member, new string('a', 150));

            // Its kick notice ("was removed from the group") also goes to the group, in either order.
            var told = chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId && m.Text.Contains("too long")).Text;
            var line = (await LogsAsync(chat)).Single();
            // In the log the line sits inside the command's own code block, where an id cannot be code again.
            StringAssert.Contains(line, told.Replace("<code>", "").Replace("</code>", ""));
            StringAssert.Contains(line, "Enforcer");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ARemovedWarn_KeepsItsReadableLine_AndNoOther()
        {
            await using var chat = await WithLogChannelAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "rude");
            await chat.SendAsync(admin, "/warn", replyTo: post);
            Assert.AreEqual(1, (await LogsAsync(chat)).Length);

            await chat.SendAsync(admin, $"/removewarn {member.Id} appeal accepted");

            var logs = await LogsAsync(chat);
            Assert.AreEqual(2, logs.Length, string.Join("\n---\n", logs));
            StringAssert.Contains(logs[1], "appeal accepted");
            Assert.IsFalse(logs[1].Contains("/removewarn"), "the readable line, not the command");
            AssertNoErrors(chat);
        }
    }
}
