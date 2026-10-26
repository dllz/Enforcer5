using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Enforcement;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Where commands, buttons and @admin reports pass the <see cref="CommandLimiter"/> before they
    /// run, and where its notices go out. Replaces the old SpamDetection thread and its
    /// <c>spammers{id}</c> ban.
    /// </summary>
    internal static class CommandGate
    {
        private sealed class Charge
        {
            public long UserId;
            public long GroupId;
            public double Cost;
            public int Refunded;
        }

        internal static CommandLimiter Limiter { get; private set; } = new CommandLimiter();

        private static readonly AsyncLocal<Charge> Current = new AsyncLocal<Charge>();

        // Users whose slow lane state was looked up in Redis lately and who had none; saves a read
        // per command for everyone else.
        private static readonly ConcurrentDictionary<long, DateTime> CheckedRedis = new ConcurrentDictionary<long, DateTime>();
        private static readonly TimeSpan RecheckRedis = TimeSpan.FromMinutes(10);

        /// <summary>A command or @admin report. False: drop it, the user was told if need be.</summary>
        internal static bool Admit(Message message) =>
            Admit(message.From.Id, message.Chat.Type == ChatType.Private ? 0 : message.Chat.Id, 1, message, null);

        /// <summary>A button press. False: drop it, the user was told if need be.</summary>
        internal static bool Admit(CallbackQuery query)
        {
            var chat = query.Message?.Chat;
            var groupId = chat == null || chat.Type == ChatType.Private ? 0 : chat.Id;
            return Admit(query.From.Id, groupId, Limiter.Limits.ButtonCost, null, query);
        }

        private static bool Admit(long userId, long groupId, double cost, Message message, CallbackQuery query)
        {
            CommandDecision decision;
            try
            {
                var stored = LoadSlowLane(userId);
                var adminChecked = (bool?)null;
                bool IsAdmin() => adminChecked ??= Constants.Devs.Contains(userId) || Methods.IsGlobalAdmin(userId) ||
                                                   (groupId != 0 && Methods.IsGroupAdmin(userId, groupId));
                decision = Limiter.Check(userId, groupId, cost, IsAdmin, stored);
            }
            catch (Exception e)
            {
                // Fail open: a broken limiter must not silence the bot.
                ErrorReporter.Report(e, "Command limiter");
                return true;
            }

            if (decision.SlowLane != null)
            {
                var state = decision.SlowLane;
                _ = SaveSlowLaneAsync(userId, state);
            }

            if (decision.Allowed)
            {
                Current.Value = new Charge { UserId = userId, GroupId = groupId, Cost = cost };
                return true;
            }

            if (decision.Notify)
            {
                try
                {
                    Tell(decision, userId, groupId, message, query);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, "Telling someone to slow down");
                }
            }
            if (decision.AlertOwner)
                Bot.Send($"#throttle Commands paused in {groupId} for {CommandLimiter.FormatWait(decision.RetryIn)}: too many at once.",
                    Bot.ErrorChatId);
            LogHelper.Info($"Throttled {userId} in {groupId}: {decision.Scope} for {CommandLimiter.FormatWait(decision.RetryIn)}");
            return false;
        }

        /// <summary>
        /// The command running on this flow carried out real enforcement: it gives its tokens back,
        /// once. Called by the enforcement queue.
        /// </summary>
        internal static void RefundCurrent()
        {
            var charge = Current.Value;
            if (charge == null || Interlocked.Exchange(ref charge.Refunded, 1) != 0) return;
            Limiter.Refund(charge.UserId, charge.GroupId, charge.Cost);
        }

        /// <summary>The enforcement this command asked for was refused after all: it was a wasted call.</summary>
        internal static void ChargeBackCurrent()
        {
            var charge = Current.Value;
            if (charge == null || Interlocked.Exchange(ref charge.Refunded, 0) != 1) return;
            Limiter.Charge(charge.UserId, charge.GroupId, charge.Cost);
        }

        private static SlowLaneState LoadSlowLane(long userId)
        {
            if (Limiter.InSlowLane(userId).HasValue) return null;
            var now = Clock.UtcNow;
            if (CheckedRedis.TryGetValue(userId, out var at) && now - at < RecheckRedis) return null;
            try
            {
                var state = Repositories.SlowLane.GetAsync(userId).GetAwaiter().GetResult();
                if (state == null) CheckedRedis[userId] = now;
                return state;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Reading the slow lane of {userId}");
                return null;
            }
        }

        private static async Task SaveSlowLaneAsync(long userId, SlowLaneState state)
        {
            try
            {
                CheckedRedis.TryRemove(userId, out _);
                await Repositories.SlowLane.SaveAsync(userId, state);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Saving the slow lane of {userId}");
            }
        }

        // One notice per block, deleted once it is over so the group is not left with them.
        private static void Tell(CommandDecision decision, long userId, long groupId, Message message, CallbackQuery query)
        {
            var chatId = message?.Chat.Id ?? query?.Message?.Chat.Id ?? userId;
            var lang = Methods.GetGroupLanguage(groupId != 0 ? groupId : chatId).Doc;
            var name = (message?.From ?? query?.From)?.FirstName ?? userId.ToString();
            var wait = CommandLimiter.FormatWait(decision.RetryIn);
            string text;
            switch (decision.Scope)
            {
                case ThrottleScope.Group:
                    text = Methods.GetLocaleString(lang, "commandsPausedGroup", wait);
                    break;
                case ThrottleScope.SlowLane:
                    text = Methods.GetLocaleString(lang, "commandsSlowLane", name,
                        (int)Limiter.Limits.SlowLaneEvery.TotalMinutes, (int)Limiter.Limits.SlowLaneQuiet.TotalMinutes);
                    break;
                default:
                    text = Methods.GetLocaleString(lang, "commandsSlowDown", name, wait);
                    break;
            }

            if (query != null && decision.Scope != ThrottleScope.Group)
            {
                // A button: the answer pops up for them alone, nothing in the group.
                Bot.Api.AnswerCallbackQuery(query.Id, HtmlFree(text), showAlert: decision.Scope == ThrottleScope.SlowLane);
                return;
            }
            var sent = message != null && decision.Scope != ThrottleScope.Group
                ? Bot.SendReply(text, message)
                : Bot.Send(text, chatId);
            if (sent != null && chatId < 0)
            {
                var keep = decision.Scope == ThrottleScope.SlowLane ? Limiter.Limits.SlowLaneEvery : decision.RetryIn;
                var delete = EnforcementJob.Delete(chatId, sent.MessageId);
                delete.NotBeforeUtc = Clock.UtcNow + keep;
                _ = EnforcementQueue.SubmitAsync(delete);
            }
        }

        private static string HtmlFree(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]+>", "");

        /// <summary>Forgets idle buckets once a minute.</summary>
        internal static void StartPruning(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await Clock.WaitAsync(TimeSpan.FromMinutes(1), ct);
                    Limiter.Prune();
                    var now = Clock.UtcNow;
                    foreach (var stale in CheckedRedis.Where(p => now - p.Value >= RecheckRedis).Select(p => p.Key).ToList())
                        CheckedRedis.TryRemove(stale, out _);
                }
            }, stop, "CommandLimiterPrune");

        /// <summary>A fresh limiter, for tests.</summary>
        internal static void ResetForTests(CommandLimits limits = null)
        {
            Limiter = new CommandLimiter(limits);
            CheckedRedis.Clear();
        }
    }
}
