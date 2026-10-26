using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Enforcer5.Tests.RateLimits
{
    [TestClass]
    public class FloodControlTests
    {
        private static readonly DateTime Start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private DateTime _now;
        private FloodControl _flood;

        [TestInitialize]
        public void SetUp()
        {
            _now = Start;
            _flood = new FloodControl(() => _now);
        }

        [DataTestMethod]
        [DataRow("sendMessage", (int)Lane.Message)]
        [DataRow("editMessageText", (int)Lane.Message)]
        [DataRow("forwardMessage", (int)Lane.Message)]
        [DataRow("copyMessage", (int)Lane.Message)]
        [DataRow("sendPhoto", (int)Lane.Message)]
        [DataRow("sendChatAction", (int)Lane.Other)]
        [DataRow("banChatMember", (int)Lane.Moderation)]
        [DataRow("unbanChatMember", (int)Lane.Moderation)]
        [DataRow("restrictChatMember", (int)Lane.Moderation)]
        [DataRow("setChatPermissions", (int)Lane.Moderation)]
        [DataRow("deleteMessage", (int)Lane.Moderation)]
        [DataRow("deleteMessages", (int)Lane.Moderation)]
        [DataRow("banChatSenderChat", (int)Lane.Moderation)]
        [DataRow("answerCallbackQuery", (int)Lane.Answer)]
        [DataRow("answerInlineQuery", (int)Lane.Answer)]
        [DataRow("getChatMember", (int)Lane.Other)]
        [DataRow("getUpdates", (int)Lane.Other)]
        public void Lanes(string method, int lane) => Assert.AreEqual((Lane)lane, FloodControl.LaneOf(method));

        [TestMethod]
        public void PrivateChat_BurstOfThree_ThenOneASecond()
        {
            var turns = Enumerable.Range(0, 5).Select(_ => _flood.TakeTurn(Lane.Message, "42")).ToList();

            CollectionAssert.AreEqual(new[] { 0.0, 0, 0, 3, 3 }, turns.Select(t => t.Wait.TotalSeconds).ToArray());
            Assert.IsTrue(turns.Skip(3).All(t => t.HasReservation && !t.Refused));
        }

        [TestMethod]
        public void Group_TwentyAMinute_InAnyMinute()
        {
            _flood.ChatMessages = 1000; // only the group window in play
            for (var i = 0; i < 20; i++)
            {
                Assert.AreEqual(TimeSpan.Zero, _flood.TakeTurn(Lane.Message, "-100").Wait, $"message {i}");
                _now = _now.AddSeconds(1);
            }
            // Twenty went out in the last 20 seconds: the next fits when the first leaves the minute.
            var next = _flood.TakeTurn(Lane.Message, "-100");
            Assert.IsTrue(next.Refused, "40 seconds is more than a message waits");
            Assert.AreEqual(40, next.RetryAfterSeconds);

            _now = _now.AddSeconds(35);
            var turn = _flood.TakeTurn(Lane.Message, "-100");
            Assert.AreEqual(TimeSpan.FromSeconds(5), turn.Wait);
            Assert.IsTrue(turn.HasReservation);
        }

        [TestMethod]
        public void Bot_TwentyFiveMessagesASecond_AcrossChats()
        {
            for (var i = 0; i < 25; i++)
                Assert.AreEqual(TimeSpan.Zero, _flood.TakeTurn(Lane.Message, (1000 + i).ToString()).Wait);

            var next = _flood.TakeTurn(Lane.Message, "2000");
            Assert.AreEqual(TimeSpan.FromSeconds(1), next.Wait);
            Assert.IsFalse(next.HasReservation, "the bot-wide window is asked again, not reserved");
            Assert.AreEqual(TimeSpan.Zero, _flood.TakeTurn(Lane.Moderation, "-100").Wait, "moderation has its own budget");
        }

        [TestMethod]
        public void Flood_PausesThatLaneInThatChat_Only()
        {
            _flood.OnFlood(Lane.Message, "sendMessage", "-100", 30);

            Assert.IsTrue(_flood.TakeTurn(Lane.Message, "-100").Refused, "30s is more than a message waits");
            Assert.AreEqual(TimeSpan.Zero, _flood.TakeTurn(Lane.Message, "-200").Wait, "other chats go on");
            Assert.AreEqual(TimeSpan.Zero, _flood.TakeTurn(Lane.Moderation, "-100").Wait, "bans in that chat go on");

            _now = _now.AddSeconds(25);
            Assert.AreEqual(TimeSpan.FromSeconds(5), _flood.TakeTurn(Lane.Message, "-100").Wait, "a short rest is waited out");
            _now = _now.AddSeconds(5);
            Assert.AreEqual(TimeSpan.Zero, _flood.TakeTurn(Lane.Message, "-100").Wait);
        }

        [TestMethod]
        public void Moderation_NeverWaitsOutAPause()
        {
            _flood.OnFlood(Lane.Moderation, "banChatMember", "-100", 2);

            var turn = _flood.TakeTurn(Lane.Moderation, "-100");

            Assert.IsTrue(turn.Refused, "the enforcement queue retries it; the caller is not held");
            Assert.AreEqual(2, turn.RetryAfterSeconds);
            Assert.IsFalse(_flood.ShouldRetry(Lane.Moderation, 1));
        }

        [TestMethod]
        public void FloodWithoutAChat_PausesTheLaneBotWide()
        {
            _flood.OnFlood(Lane.Message, "sendMessage", "", 60);

            Assert.IsTrue(_flood.TakeTurn(Lane.Message, "-100").Refused);
            Assert.IsTrue(_flood.TakeTurn(Lane.Message, "42").Refused);
            Assert.IsFalse(_flood.TakeTurn(Lane.Moderation, "-100").Refused);
        }

        [TestMethod]
        public void ShortFloods_AreRetried_LongOnesAreNot()
        {
            Assert.IsTrue(_flood.ShouldRetry(Lane.Message, 10));
            Assert.IsFalse(_flood.ShouldRetry(Lane.Message, 11));
            Assert.IsTrue(_flood.ShouldRetry(Lane.Answer, 3));
            Assert.IsFalse(_flood.ShouldRetry(Lane.Answer, 4));
        }

        [TestMethod]
        public void Flooded_IsRaised_ImposedPausesAreNot()
        {
            var raised = new List<(Lane, string, string, int)>();
            _flood.Flooded += (lane, method, chat, seconds) => raised.Add((lane, method, chat, seconds));

            _flood.OnFlood(Lane.Message, "sendMessage", "-100", 0);
            _flood.ImposePause(Lane.Moderation, "-200", _now.AddSeconds(30));

            CollectionAssert.AreEqual(new[] { (Lane.Message, "sendMessage", "-100", 1) }, raised);
            Assert.IsTrue(_flood.TakeTurn(Lane.Moderation, "-200").Refused, "a pause heard from elsewhere is kept too");
        }
    }

    [TestClass]
    [DoNotParallelize] // Clock is static
    public class TelegramGatewayTests
    {
        private const long Group = -1001;
        private FakeClock _clock;
        private FakeTelegram _telegram;
        private TelegramGateway _gateway;

        [TestInitialize]
        public void SetUp()
        {
            _clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
            Clock.UseForTests(_clock);
            _telegram = new FakeTelegram(HarnessChat.BotUser, _clock);
            _gateway = new TelegramGateway(_telegram, new FloodControl());
        }

        [TestCleanup]
        public void Cleanup() => Clock.UseForTests(null);

        // Runs the simulated clock until every task is done.
        private async Task Drive(params Task[] tasks)
        {
            for (var i = 0; i < 100000 && !tasks.All(t => t.IsCompleted); i++)
            {
                await Task.Delay(1);
                if (tasks.All(t => t.IsCompleted)) break;
                if (_clock.NextDue is { } next) _clock.AdvanceTo(next);
            }
            Assert.IsTrue(tasks.All(t => t.IsCompleted), "Did not finish");
        }

        private static async Task<bool> Sent(Task send)
        {
            try
            {
                await send;
                return true;
            }
            catch (ApiRequestException e) when (e.ErrorCode == 429)
            {
                return false;
            }
        }

        [TestMethod]
        public async Task ABurstToOneGroup_StaysUnderTelegramsLimits()
        {
            var sends = Enumerable.Range(0, 30).Select(i => Sent(_gateway.SendMessage(Group, $"m{i}"))).ToArray();
            await Drive(sends);

            var delivered = sends.Count(s => s.Result);
            Assert.AreEqual(0, _telegram.LimitRefusals.Count, "Telegram never had to say 429");
            Assert.IsTrue(delivered >= 3 && delivered <= 20, $"{delivered} went out");
            Assert.AreEqual(30 - delivered, sends.Count(s => !s.Result), "the rest were refused locally, not queued for minutes");
        }

        [TestMethod]
        public async Task AShort429_IsWaitedOut_AndRetriedOnce()
        {
            _telegram.StartMessageStorm(TimeSpan.FromSeconds(5), retryAfter: 5);

            var send = Sent(_gateway.SendMessage(Group, "hello"));
            await Drive(send);

            Assert.IsTrue(send.Result);
            Assert.AreEqual(2, _telegram.Calls.Count(c => c.Method == "sendMessage"));
        }

        [TestMethod]
        public async Task ALong429_FailsTheMessageAtOnce_ButBansGoOn()
        {
            _telegram.SetMember(Group, 42, MemberState.Member);
            _telegram.StartMessageStorm(TimeSpan.FromMinutes(5), retryAfter: 300);

            var first = Sent(_gateway.SendMessage(Group, "hello"));
            await Drive(first);
            var second = Sent(_gateway.SendMessage(Group, "again"));
            await Drive(second);
            await _gateway.BanChatMember(Group, 42);

            Assert.IsFalse(first.Result);
            Assert.IsFalse(second.Result);
            Assert.AreEqual(1, _telegram.Calls.Count(c => c.Method == "sendMessage"), "the second never reached Telegram");
            Assert.AreEqual(MemberState.Kicked, _telegram.GetMember(Group, 42));
        }

        [TestMethod]
        public async Task A429OnModeration_IsHandedBack_AndPausesModerationInThatChat()
        {
            _telegram.SetMember(Group, 42, MemberState.Member);
            _telegram.StartStorm(c => c.Method == "banChatMember", TimeSpan.FromSeconds(20), retryAfter: 20);

            var first = await Assert.ThrowsExceptionAsync<ApiRequestException>(() => _gateway.BanChatMember(Group, 42));
            var second = await Assert.ThrowsExceptionAsync<LocalRateLimitException>(() => _gateway.BanChatMember(Group, 42));

            Assert.AreEqual(429, first.ErrorCode);
            Assert.AreEqual(20, second.Parameters.RetryAfter);
            Assert.AreEqual(1, _telegram.Calls.Count(c => c.Method == "banChatMember"), "the second never reached Telegram");
        }
    }
}
