using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Enforcer5.Tests
{
    /// <summary>
    /// The harness's own parts: the simulated clock, Telegram's limits and the Redis stand-in. No
    /// database needed, so these run in the Docker build.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // Clock is static
    public class HarnessInfrastructureTests
    {
        private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        [TestCleanup]
        public void Cleanup() => Clock.UseForTests(null);

        [TestMethod]
        public void FakeClock_FiresTimersInOrder_AtTheirOwnTime()
        {
            var clock = new FakeClock(Start);
            var fired = new System.Collections.Generic.List<(string, DateTimeOffset)>();
            clock.CreateTimer(_ => fired.Add(("b", clock.GetUtcNow())), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            clock.CreateTimer(_ => fired.Add(("a", clock.GetUtcNow())), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            clock.CreateTimer(_ => fired.Add(("p", clock.GetUtcNow())), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

            clock.Advance(TimeSpan.FromSeconds(7));

            CollectionAssert.AreEqual(new[]
            {
                ("a", Start.AddSeconds(1)), ("b", Start.AddSeconds(2)), ("p", Start.AddSeconds(3)), ("p", Start.AddSeconds(6))
            }, fired);
            Assert.AreEqual(Start.AddSeconds(7), clock.GetUtcNow());
            Assert.AreEqual(Start.AddSeconds(9), clock.NextDue);
        }

        [TestMethod]
        public async Task Clock_WaitAsync_EndsWhenTheSimulatedTimeIsUp_OrWhenWoken()
        {
            var clock = new FakeClock(Start);
            Clock.UseForTests(clock);

            var sleeper = Clock.WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(9));
            Assert.IsFalse(sleeper.IsCompleted);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.IsFalse(await sleeper, "Not woken: the time was up");

            using var wake = new CancellationTokenSource();
            var woken = Clock.WaitAsync(TimeSpan.FromHours(1), wake.Token);
            wake.Cancel();
            Assert.IsTrue(await woken);
            Assert.IsNull(clock.NextDue, "Waking disposes the timer");
        }

        [TestMethod]
        public async Task Clock_CountsDispatchedWorkWaitingOnIt_AsParked()
        {
            var clock = new FakeClock(Start);
            Clock.UseForTests(clock);
            var done = 0;

            Bot.Dispatch(async () =>
            {
                await Clock.Delay(TimeSpan.FromSeconds(30));
                Interlocked.Increment(ref done);
            });

            await Bot.WaitIdleAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, Clock.Parked);
            Assert.AreEqual(0, done);

            clock.Advance(TimeSpan.FromSeconds(30));
            await Bot.WaitIdleAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, Clock.Parked);
            Assert.AreEqual(1, done);
        }

        [TestMethod]
        public async Task FakeTelegram_GoingOverTheGroupLimit_Answers429_UntilASlotFrees()
        {
            var clock = new FakeClock(Start);
            var telegram = new FakeTelegram(HarnessChat.BotUser, clock);
            telegram.Limits.Enabled = true;
            telegram.Limits.ChatMessages = 100; // only the group limit in play
            const long group = -100500;

            for (var i = 0; i < 20; i++)
            {
                await telegram.SendMessage(group, $"m{i}");
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            var refused = await Assert.ThrowsExceptionAsync<ApiRequestException>(() => telegram.SendMessage(group, "one too many"));

            Assert.AreEqual(429, refused.ErrorCode);
            Assert.AreEqual(40, refused.Parameters.RetryAfter, "The first message leaves the minute 40 seconds from now");
            clock.Advance(TimeSpan.FromSeconds(40));
            await telegram.SendMessage(group, "fits again");
            Assert.AreEqual(1, telegram.LimitRefusals.Count);
        }

        [TestMethod]
        public async Task FakeTelegram_Storm_RefusesMatchingCalls_ForItsDuration()
        {
            var clock = new FakeClock(Start);
            var telegram = new FakeTelegram(HarnessChat.BotUser, clock);
            const long group = -100500;
            telegram.SetMember(group, 42, MemberState.Member);
            telegram.StartMessageStorm(TimeSpan.FromMinutes(5), retryAfter: 30);

            var refused = await Assert.ThrowsExceptionAsync<ApiRequestException>(() => telegram.SendMessage(group, "hello"));
            await telegram.BanChatMember(group, 42); // moderation is not in this storm

            Assert.AreEqual(30, refused.Parameters.RetryAfter);
            Assert.AreEqual(MemberState.Kicked, telegram.GetMember(group, 42));
            clock.Advance(TimeSpan.FromMinutes(5));
            await telegram.SendMessage(group, "over");
            Assert.IsTrue(telegram.Calls.Single(c => c.Refused).Injected);
        }

        [TestMethod]
        public async Task FakeTelegram_BanWithRevoke_RemovesTheirMessages_AndAKickIsBanThenUnban()
        {
            var telegram = new FakeTelegram(HarnessChat.BotUser, new FakeClock(Start));
            const long group = -100500;
            telegram.SetMember(group, 1, MemberState.Member);
            telegram.SetMember(group, 2, MemberState.Member);
            telegram.Posted(group, 10, 1);
            telegram.Posted(group, 11, 1);
            telegram.Posted(group, 12, 2);

            await telegram.BanChatMember(group, 1, revokeMessages: true);
            await telegram.UnbanChatMember(group, 2);

            Assert.AreEqual(0, telegram.MessagesFrom(group, 1).Count);
            Assert.AreEqual(MemberState.Kicked, telegram.GetMember(group, 1));
            Assert.AreEqual(MemberState.Left, telegram.GetMember(group, 2), "Unban without only_if_banned removes a member");
            Assert.AreEqual(1, telegram.MessagesFrom(group, 2).Count);
        }

        [TestMethod]
        public async Task FakeRedisKeys_ExpireOnTheSimulatedClock()
        {
            var clock = new FakeClock(Start);
            var hashes = new FakeRedisHashes();
            var keys = new FakeRedisKeys(clock, hashes);
            var redis = new Mock<IDatabase>();
            hashes.Install(redis);
            keys.Install(redis);
            var db = redis.Object;

            await db.StringSetAsync("counter", 1, TimeSpan.FromSeconds(6), false, When.Always, CommandFlags.None);
            Assert.AreEqual(2, await db.StringIncrementAsync("counter", 1, CommandFlags.None));
            await db.SortedSetAddAsync("due", "job1", 5, CommandFlags.None);
            await db.SortedSetAddAsync("due", "job2", 1, CommandFlags.None);

            clock.Advance(TimeSpan.FromSeconds(6));

            Assert.IsTrue((await db.StringGetAsync("counter", CommandFlags.None)).IsNull);
            var due = await db.SortedSetRangeByScoreAsync("due", 0, 3, Exclude.None, Order.Ascending, 0, -1, CommandFlags.None);
            CollectionAssert.AreEqual(new[] { "job2" }, due.Select(v => v.ToString()).ToArray());
        }
    }
}
