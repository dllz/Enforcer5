using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Requests;

namespace Enforcer5.Tests.Harness
{
    /// <summary>The group flood check, driven through real messages. Needs the test database.</summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class FloodScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static void FloodOn(HarnessChat chat, string action, int max = 5)
        {
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no"; // "yes" switches it off
            chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = max.ToString();
            chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = action;
        }

        [TestMethod]
        public async Task AFlood_IsKickedOnce_AndEveryMessageOfItGoes()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "kick");
            var flooder = chat.Add("Flooder");
            var bystander = chat.Add("Bystander");
            await chat.SendAsync(bystander, "hello all");

            // Nine at once: the consumers handle them side by side, as in a real burst.
            for (var i = 0; i < 9; i++) chat.Deliver(chat.Text(flooder, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, flooder.Id));
            Assert.AreEqual(1, chat.Telegram.Accepted.Count(c => c.Method == "banChatMember"), "kicked once, not once per message");
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, flooder.Id).Count, "the flood is gone");
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, bystander.Id).Count, "nobody else's");
            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains("Flooder")));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AFloodBan_TakesTheirMessagesInTheSameCall()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "ban");
            var flooder = chat.Add("Flooder");

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Text(flooder, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            var ban = (BanChatMemberRequest)chat.Telegram.Accepted.Single(c => c.Method == "banChatMember").Request;
            Assert.IsTrue(ban.RevokeMessages);
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, flooder.Id).Count);
            Assert.AreEqual(0, chat.Telegram.Accepted.Count(c => c.Method.StartsWith("deleteMessage")), "no separate deletes needed");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnOrdinaryConversation_IsNeverAFlood()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "kick", max: 3);
            var talker = chat.Add("Talker");

            for (var i = 0; i < 20; i++)
            {
                await chat.SendAsync(talker, $"line {i}");
                await chat.AdvanceAsync(TimeSpan.FromSeconds(2.5));
            }

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, talker.Id));
            Assert.AreEqual(20, chat.Telegram.MessagesFrom(chat.ChatId, talker.Id).Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task FloodCheckingOff_LeavesItAlone()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "kick");
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "yes";
            var flooder = chat.Add("Flooder");

            for (var i = 0; i < 10; i++) chat.Deliver(chat.Text(flooder, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, flooder.Id));
            AssertNoErrors(chat);
        }
    }
}
