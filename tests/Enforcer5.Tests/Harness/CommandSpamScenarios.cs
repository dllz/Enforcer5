using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Command, button and @admin spam against the command limiter and its slow lane, driven through
    /// real updates. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class CommandSpamScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        // /id without a reply answers with the chat's id.
        private static int Answers(HarnessChat chat, HarnessUser user) =>
            chat.Telegram.Accepted.Count(c => c.Method == "sendMessage" && c.ChatId == chat.ChatId &&
                                              c.Text.StartsWith(chat.ChatId.ToString(), StringComparison.Ordinal));

        [TestMethod]
        public async Task CommandSpam_EndsInTheSlowLane_ToldOnce_AndBackToNormalAfterQuiet()
        {
            await using var chat = await HarnessChat.StartAsync();
            var spammer = chat.Add("Spammer");

            for (var i = 0; i < 40; i++)
            {
                chat.Deliver(chat.Text(spammer, "/id"));
                await chat.SettleAsync(flush: false);
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            var bucket = (int)Enforcer5.Helpers.CommandGate.Limiter.Limits.UserBurst;
            Assert.AreEqual(bucket, Answers(chat, spammer), "a bucket's worth, then nothing");
            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains("slow down please")), "told to slow down once");
            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains("slow lane")), "told about the slow lane once");
            Assert.IsNotNull(chat.Keys[$"slowlane:{spammer.Id}"], "kept in Redis");

            // In the slow lane: one answer every five minutes, however often they try.
            for (var minute = 0; minute < 10; minute++)
            {
                chat.Deliver(chat.Text(spammer, "/id"));
                await chat.AdvanceAsync(TimeSpan.FromMinutes(1));
            }
            Assert.AreEqual(bucket + 1, Answers(chat, spammer), "one more in ten minutes: five minutes after they went in");

            // Fifteen quiet minutes and they are a normal user again.
            await chat.AdvanceAsync(TimeSpan.FromMinutes(16));
            await chat.SendAsync(spammer, "/id");
            await chat.SendAsync(spammer, "/id");
            Assert.AreEqual(bucket + 3, Answers(chat, spammer));
            Assert.AreEqual(0, chat.Telegram.LimitRefusals.Count);
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, spammer.Id), "never banned for it");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdminBanningFiftyRaiders_IsNeverSlowed()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var raiders = Enumerable.Range(0, 50).Select(i => chat.Add($"Raider{i}")).ToList();

            foreach (var raider in raiders)
            {
                chat.Deliver(chat.Text(admin, $"/ban {raider.Id} raid"));
                await chat.SettleAsync(flush: false);
            }
            await chat.AdvanceAsync(TimeSpan.FromMinutes(5));

            Assert.IsTrue(raiders.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Kicked),
                $"every ban carried out: {raiders.Count(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Kicked)} of 50; " +
                string.Join(" | ", chat.Replies.Distinct().Take(5)) + " | " + string.Join(",", chat.Telegram.Calls.GroupBy(c => c.Method).Select(g => $"{g.Key}={g.Count()}")));
            Assert.AreEqual(0, chat.Replies.Count(r => r.Contains("slow down please") || r.Contains("slow lane")));
            Assert.AreEqual(0, chat.Telegram.LimitRefusals.Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdminBanningAnotherAdminAgainAndAgain_WastesOneCall_AndIsSlowedNotSlowLaned()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("OtherAdmin", MemberState.Administrator);

            for (var i = 0; i < 30; i++)
            {
                chat.Deliver(chat.Text(admin, $"/ban {other.Id}"));
                await chat.SettleAsync(flush: false);
            }
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));

            Assert.AreEqual(1, chat.Telegram.Calls.Count(c => c.Method == "banChatMember"), "Telegram asked once");
            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains("slow down please")));
            Assert.AreEqual(0, chat.Replies.Count(r => r.Contains("slow lane")), "admins never go there");
            Assert.AreEqual(MemberState.Administrator, chat.Telegram.GetMember(chat.ChatId, other.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ButtonSpam_IsAnsweredPrivately_AndStopsReachingTheMenu()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            // The flood settings menu, in the admin's private chat with the bot.
            var menu = new Telegram.Bot.Types.Message
            {
                Id = chat.Telegram.NextMessageId(),
                Chat = new Telegram.Bot.Types.Chat { Id = admin.Id, Type = Telegram.Bot.Types.Enums.ChatType.Private },
                From = HarnessChat.BotUser,
                Text = "Flood settings"
            };

            // As fast as a thumb goes: no time passes between presses.
            for (var i = 0; i < 40; i++)
            {
                chat.DeliverPress(admin, menu, $"floodraise:{chat.ChatId}");
                await chat.SettleAsync(flush: false);
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            var raised = int.Parse(chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"]);
            var bucket = (int)(Enforcer5.Helpers.CommandGate.Limiter.Limits.UserBurst / Enforcer5.Helpers.CommandGate.Limiter.Limits.ButtonCost);
            Assert.AreEqual(bucket, raised, "a bucket's worth of presses at half a command each");
            Assert.AreEqual(1, chat.Telegram.Accepted.Count(c => c.Method == "answerCallbackQuery" && c.Text?.Contains("slow down please") == true),
                "told once, in a popup only they see");
            Assert.AreEqual(0, chat.Telegram.LimitRefusals.Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AdminReportSpam_StillGetsOneReportThroughEveryFiveMinutes()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            var spammer = chat.Add("Spammer");
            var target = chat.Add("Target");
            var bad = await chat.SendAsync(target, "something bad");

            for (var i = 0; i < 30; i++)
            {
                chat.Deliver(chat.Text(spammer, "@admin look", replyTo: bad));
                await chat.SettleAsync(flush: false);
            }
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));
            var before = chat.Telegram.Accepted.Count(c => c.Method == "sendMessage");

            for (var minute = 0; minute < 10; minute++)
            {
                chat.Deliver(chat.Text(spammer, "@admin look", replyTo: bad));
                await chat.AdvanceAsync(TimeSpan.FromMinutes(1));
            }

            var after = chat.Telegram.Accepted.Count(c => c.Method == "sendMessage");
            Assert.IsTrue(after - before <= 6, $"{after - before} messages for ten minutes of reports");
            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains("slow lane")));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task SomeoneInTheSlowLane_StillGetsMuteOnJoin()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:settings", "MuteOnJoin"] = "no"; // "no" switches it on
            var spammer = chat.Add("Spammer");
            for (var i = 0; i < 30; i++)
            {
                chat.Deliver(chat.Text(spammer, "/id"));
                await chat.SettleAsync(flush: false);
            }

            var joiner = chat.Add("Joiner", MemberState.Left, id: spammer.Id + 1);
            chat.Keys[$"slowlane:{joiner.Id}"] = "{}";
            await chat.JoinAsync(joiner);

            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, joiner.Id));
            AssertNoErrors(chat);
        }
    }
}
