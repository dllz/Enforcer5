using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// What an admin's command posts to the group's log channel. A command that names its member by id,
    /// mention or @username logs that member's id, as a reply does: the 2026 Redis recovery could not
    /// match "/warn @name" posts to anyone once the username had changed.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class LogCommandScenarios
    {
        private const long LogChannel = -1004444;

        private static async Task<HarnessChat> WithLogChannelAsync()
        {
            var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet("logChatGroups", chat.ChatId.ToString());
            chat.Hashes[$"chat:{chat.ChatId}:settings", "logchat"] = LogChannel.ToString();
            return chat;
        }

        private static string LastLog(HarnessChat chat) =>
            chat.Telegram.SentMessages.Last(m => m.Chat.Id == LogChannel).Text;

        [TestMethod]
        public async Task AWarnByUsername_LogsTheMembersId()
        {
            await using var chat = await WithLogChannelAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member", username: "somebody");
            chat.Hashes["bot:usernames", "@somebody"] = member.Id.ToString();
            chat.Hashes[$"user:{member.Id}", "name"] = "Member";

            await chat.SendAsync(admin, "/warn @somebody spam");

            StringAssert.Contains(LastLog(chat), $"Member ({member.Id})");
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task AWarnById_WithNoStoredName_LogsTheId()
        {
            await using var chat = await WithLogChannelAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            await chat.SendAsync(admin, $"/prewarn {member.Id}");

            StringAssert.Contains(LastLog(chat), member.Id.ToString());
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task AWarnAsAReply_StillLogsTheRepliedTo()
        {
            await using var chat = await WithLogChannelAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "hello");

            await chat.SendAsync(admin, "/warn", replyTo: post);

            StringAssert.Contains(LastLog(chat), $"Member ({member.Id})");
        }
    }
}
