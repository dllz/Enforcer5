using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Telegram's limits and 429s, driven through real commands in a simulated group: enforcement
    /// never waits behind messages, and messages that cannot go out are merged and sent once the
    /// pause is over. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class RateLimitScenarios
    {
        private static void AssertOnly429s(HarnessChat chat)
        {
            var other = chat.Errors.Where(e => !e.Contains("Too Many Requests")).ToList();
            Assert.AreEqual(0, other.Count, "Errors reported:\n" + string.Join("\n", other));
        }

        [TestMethod]
        public async Task MessageStorm_KicksStillHappen_AndTheAnnouncementsGoOutAsOne()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var members = Enumerable.Range(0, 4).Select(i => chat.Add($"Member{i}")).ToList();
            chat.Telegram.StartMessageStorm(TimeSpan.FromMinutes(2), retryAfter: 120);

            // Delivered without letting time pass, so the storm is still on afterwards.
            foreach (var member in members)
            {
                chat.Deliver(chat.Text(admin, $"/kick {member.Id}"));
                await chat.SettleAsync();
            }

            Assert.IsTrue(members.All(m => chat.Telegram.GetMember(chat.ChatId, m.Id) == MemberState.Left), "every kick went through");
            Assert.AreEqual(0, chat.Replies.Count, "nothing could be posted yet");

            await chat.AdvanceAsync(TimeSpan.FromMinutes(3));

            Assert.AreEqual(1, chat.Replies.Count, "one merged message once the pause is over");
            Assert.AreEqual(4, chat.Replies.Single().Split("\n\n").Length);
            Assert.AreEqual(1, chat.Telegram.Calls.Count(c => c.Method == "sendMessage" && c.Refused),
                "after the first 429 the bot stopped asking until the pause was over");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ABurstOfReplies_NeverEarnsALimit429()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);

            // Every /id replies; 40 of them is twice the group's minute.
            for (var i = 0; i < 40; i++)
                chat.Deliver(chat.Text(admin, "/id"));
            await chat.AdvanceAsync(TimeSpan.FromMinutes(3));

            Assert.AreEqual(0, chat.Telegram.LimitRefusals.Count, "pacing kept every reply under Telegram's limits");
            Assert.AreEqual(0, Enforcer5.Helpers.Announcements.WaitingFor(chat.ChatId), "what had to wait went out merged");
            var sent = chat.Telegram.Accepted.Where(c => c.Method == "sendMessage").Select(c => c.At).ToList();
            Assert.IsTrue(sent.All(t => sent.Count(o => o > t.AddMinutes(-1) && o <= t) <= 20), "never more than 20 in a minute");
            AssertOnly429s(chat);
        }
    }
}
