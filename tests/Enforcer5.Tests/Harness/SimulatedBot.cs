using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Moq;
using StackExchange.Redis;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The bot without PostgreSQL or update routing: simulated time, Telegram with its limits, the
    /// Redis stand-in, the gateway and the enforcement queue. For tests of those parts that must run
    /// in the Docker build, where there is no database. Takes over the same statics as
    /// <see cref="HarnessChat"/>; tests using it must not run in parallel.
    /// </summary>
    internal sealed class SimulatedBot : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _errors = new();
        private readonly Telegram.Bot.ITelegramBotClient _previousApi;
        private readonly User _previousMe;
        private readonly Action<string> _previousObserver;
        private CancellationTokenSource _queueStop = new();

        internal FakeClock Clock { get; } = FakeClock.StartingNow();
        internal FakeTelegram Telegram { get; }
        internal Mock<IDatabase> Redis { get; } = new Mock<IDatabase>();
        internal FakeRedisHashes Hashes { get; } = new FakeRedisHashes();
        internal FakeRedisKeys Keys { get; }
        internal IReadOnlyList<string> Errors => _errors.ToList();

        private SimulatedBot()
        {
            Telegram = new FakeTelegram(HarnessChat.BotUser, Clock);
            Keys = new FakeRedisKeys(Clock, Hashes);
            _previousApi = Bot.Api;
            _previousMe = Bot.Me;
            _previousObserver = ErrorReporter.Observer;
        }

        internal static async Task<SimulatedBot> StartAsync()
        {
            var bot = new SimulatedBot();
            Enforcer5.Helpers.Clock.UseForTests(bot.Clock);
            bot.Hashes.Install(bot.Redis);
            bot.Keys.Install(bot.Redis);
            Enforcer5.Helpers.Redis.UseForTests(bot.Redis.Object);
            Bot.UseClient(bot.Telegram);
            Bot.Me = HarnessChat.BotUser;
            Announcements.ResetForTests();
            CommandGate.ResetForTests();
            FloodTracker.ResetForTests();
            ErrorReporter.Observer = report => bot._errors.Enqueue(report);
            EnforcementQueue.ResetForTests();
            EnforcementQueue.Start(bot._queueStop.Token);
            await bot.SettleAsync();
            return bot;
        }

        /// <summary>A group the bot is an admin in, with these members.</summary>
        internal long Group(long chatId, params long[] members)
        {
            Telegram.SetMember(chatId, HarnessChat.BotUser.Id, MemberState.Administrator);
            foreach (var m in members) Telegram.SetMember(chatId, m, MemberState.Member);
            return chatId;
        }

        internal Task SettleAsync() => HarnessTime.SettleAsync(Clock);

        internal Task AdvanceAsync(TimeSpan by) => HarnessTime.AdvanceAsync(Clock, by);

        /// <summary>
        /// Submits <paramref name="job"/> from dispatched work, as the commands do, and gives back
        /// what the caller heard once it had waited its <see cref="EnforcementQueue.CallerWait"/>.
        /// </summary>
        internal async Task<EnforcementResult> RunAsync(EnforcementJob job)
        {
            EnforcementResult result = default;
            Bot.DispatchAction(() => result = EnforcementQueue.Run(job));
            await HarnessTime.CompleteAsync(Clock, EnforcementQueue.CallerWait + TimeSpan.FromSeconds(1));
            return result;
        }

        /// <summary>Submits without waiting for an answer; jobs submitted one after another keep their order.</summary>
        internal void Submit(EnforcementJob job) =>
            EnforcementQueue.SubmitAsync(job).GetAwaiter().GetResult();

        internal async Task CrashAndRestartAsync()
        {
            EnforcementQueue.CrashForTests();
            await SettleAsync();
            _queueStop = new CancellationTokenSource();
            EnforcementQueue.Start(_queueStop.Token);
            await SettleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _queueStop.Cancel();
            EnforcementQueue.ResetForTests();
            // Let whatever still waits on this clock run out, so nothing is left for the next test.
            try { await HarnessTime.CompleteAsync(Clock, TimeSpan.FromHours(2)); } catch (TimeoutException) { }
            Announcements.ResetForTests();
            ErrorReporter.Observer = _previousObserver;
            Bot.Api = _previousApi;
            Bot.Me = _previousMe;
            Enforcer5.Helpers.Clock.UseForTests(null);
        }
    }
}
