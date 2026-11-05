using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests
{
    /// <summary>
    /// /queues and the stuck-queue watchdog over every queue (Daniel, 2026-11-05), the enforcement queue above all: work
    /// that waits on purpose (a 429's retry_after, the bot's rights, a kick's next unban try) is never called stuck; work
    /// that should move and does not is, once, and again when it moves. No database needed: these run in the Docker build.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // the bot is static
    public class QueueStatusTests
    {
        private const long Group = -1001;

        [TestInitialize]
        public void Reset() => QueueStatus.ResetForTests();

        private static QueueStatus.Snapshot Read(string name) => QueueStatus.Queues.Single(q => q.Name == name).Read();

        // The watchdog's round every 30 s for this long; fails if anything was called stuck meanwhile.
        private static async Task WatchAsync(SimulatedBot bot, TimeSpan span)
        {
            for (var at = TimeSpan.Zero; at < span; at += QueueStatus.CheckEvery)
            {
                await bot.AdvanceAsync(QueueStatus.CheckEvery);
                QueueStatus.Report();
            }
            Assert.IsFalse(bot.Errors.Any(e => e.Contains("Queue stuck")), string.Join("\n", bot.Errors));
        }

        [TestMethod]
        public void EveryQueue_IsListed_AlsoBeforeItHadWork()
        {
            var names = QueueStatus.Queues.Select(q => q.Name).ToList();
            foreach (var name in new[] { "enforcement", "updates", "actions", "interactive", "log channels", "raid log digests",
                         "announcements", "batches", "detections", "near misses" })
                CollectionAssert.Contains(names, name);
        }

        [TestMethod]
        public async Task AQueueThatReportsItsOwnBooks_IsStuckOnlyWithDueWorkAndNoProgress_OnceAndAgainWhenItMoves()
        {
            await using var bot = await SimulatedBot.StartAsync();
            var due = 0;
            long progress = 0;
            QueueStatus.Watch("test queue", () => new QueueStatus.Reading(due, progress, $"{due} due"));
            try
            {
                // Nothing due: never stuck, however long nothing moves.
                await WatchAsync(bot, TimeSpan.FromMinutes(5));

                // Due and moving: not stuck.
                due = 3;
                for (var i = 0; i < 10; i++)
                {
                    progress++;
                    await WatchAsync(bot, TimeSpan.FromSeconds(30));
                }

                // Due and not moving: stuck after two minutes, said once.
                await WatchAsync(bot, TimeSpan.FromMinutes(1.5));
                await bot.AdvanceAsync(TimeSpan.FromMinutes(1));
                QueueStatus.Report();
                QueueStatus.Report();
                var stuck = bot.Errors.Single(e => e.Contains("Queue stuck: test queue"));
                StringAssert.Contains(stuck, "test queue: 3 due, last moved 2 min");
                Assert.IsTrue(Read("test queue").Stuck);

                progress++;
                QueueStatus.Report();
                Assert.AreEqual(1, bot.Errors.Count(e => e.Contains("Queue moving again: test queue")));
                Assert.IsFalse(Read("test queue").Stuck);
            }
            finally
            {
                QueueStatus.RemoveForTests("test queue");
            }
        }

        [TestMethod]
        public async Task TheEnforcementQueue_WaitingOutA429_IsNotStuck_AndSaysWhy()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Group, 1);
            bot.Telegram.StartFullStorm(TimeSpan.FromMinutes(3), retryAfter: 60);

            await bot.RunAsync(EnforcementJob.Ban(Group, 1));
            StringAssert.Contains(Read("enforcement").ToString(), "enforcement: 1 jobs in 1 chats, 0 running, 1 waiting out a 429");
            await WatchAsync(bot, TimeSpan.FromMinutes(4));

            Assert.AreEqual(MemberState.Kicked, bot.Telegram.GetMember(Group, 1));
            Assert.AreEqual("enforcement: 0 jobs in 0 chats, 0 running, 1 done", Read("enforcement").ToString());
        }

        [TestMethod]
        public async Task TheEnforcementQueue_WaitingForTheBotsRights_IsNotStuck()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Group, 1);
            bot.Telegram.SetBotCanRestrict(Group, false);

            await bot.RunAsync(EnforcementJob.Ban(Group, 1));
            StringAssert.Contains(Read("enforcement").ToString(), "1 waiting for rights");
            await WatchAsync(bot, TimeSpan.FromMinutes(5));
            bot.Telegram.SetBotCanRestrict(Group, true);
            await WatchAsync(bot, TimeSpan.FromMinutes(2));

            Assert.AreEqual(MemberState.Kicked, bot.Telegram.GetMember(Group, 1));
        }

        [TestMethod]
        public async Task TheEnforcementQueue_RetryingAKicksUnban_IsNotStuck()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Group, 1);
            bot.Telegram.IgnoreUnbans(Group, 8);

            await bot.RunAsync(EnforcementJob.Kick(Group, 1));
            StringAssert.Contains(Read("enforcement").ToString(), "1 kick unbans to retry");
            await WatchAsync(bot, TimeSpan.FromMinutes(9));

            Assert.AreEqual(MemberState.Left, bot.Telegram.GetMember(Group, 1));
            Assert.AreEqual(0, EnforcementQueue.Count);
        }

        [TestMethod]
        public async Task TheEnforcementQueue_WithACallThatNeverAnswers_IsStuck_AndMovesAgainWhenItDoes()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Group, 1, 2);
            bot.Telegram.SetLatency("banChatMember", TimeSpan.FromMinutes(5));

            bot.Submit(EnforcementJob.Ban(Group, 1));
            bot.Submit(EnforcementJob.Ban(Group, 2));
            for (var i = 0; i < 6; i++)
            {
                await bot.AdvanceAsync(QueueStatus.CheckEvery);
                QueueStatus.Report();
            }
            var stuck = bot.Errors.Single(e => e.Contains("Queue stuck: enforcement"));
            StringAssert.Contains(stuck, "2 jobs in 1 chats, 1 running, 1 due");

            bot.Telegram.SetLatency("banChatMember", TimeSpan.Zero);
            await bot.AdvanceAsync(TimeSpan.FromMinutes(3));
            QueueStatus.Report();
            Assert.AreEqual(1, bot.Errors.Count(e => e.Contains("Queue moving again: enforcement")));
            Assert.AreEqual(MemberState.Kicked, bot.Telegram.GetMember(Group, 2));
        }

        [TestMethod]
        public async Task TheActionGate_CountsWhatIsBusyAndWhatWaitsForAPermit()
        {
            await using var bot = await SimulatedBot.StartAsync();
            var before = Read("actions").Done;
            using var release = new ManualResetEventSlim();
            ThreadPool.GetMinThreads(out var workers, out var io);
            ThreadPool.SetMinThreads(Math.Max(workers, 80), io);
            try
            {
                for (var i = 0; i < 34; i++) Bot.DispatchAction(() => release.Wait(TimeSpan.FromSeconds(30)));
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (Read("actions").Waiting < 2 && DateTime.UtcNow < until) await Task.Delay(10);
                Assert.AreEqual($"actions: 32 of 32 busy, 2 waiting, {before} done", Read("actions").ToString().Split(", last")[0]);
            }
            finally
            {
                release.Set();
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (Read("actions").Done < before + 34 && DateTime.UtcNow < until) await Task.Delay(10);
                ThreadPool.SetMinThreads(workers, io);
            }
            Assert.AreEqual($"actions: 0 of 32 busy, 0 waiting, {before + 34} done", Read("actions").ToString());
        }
    }
}
