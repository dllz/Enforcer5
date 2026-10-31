using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Whether a bot is a member of a group, for the filters' "allow bots in this group" choice: a
    /// message sent via an inline bot is only exempt when that bot is in the group. An admin bot is
    /// known from <see cref="AdminDirectory"/>; any other is asked once (getChatMember) and the answer
    /// kept for <see cref="KeepFor"/>, one call at a time per bot and group. When Telegram does not
    /// answer, the bot counts as outside the group for <see cref="RetryAfterFailure"/>, so the
    /// message is checked as usual.
    /// </summary>
    internal static class GroupBots
    {
        internal static TimeSpan KeepFor = TimeSpan.FromHours(1);
        internal static TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
        private const int Capacity = 50_000;

        /// <param name="InGroup">Null when Telegram did not answer.</param>
        /// <param name="IsAdmin">An admin of the group (getChatAdministrators leaves other bots out).</param>
        private sealed record Answer(bool? InGroup, bool IsAdmin, DateTime Until);

        private static readonly object Lock = new();
        private static readonly Dictionary<(long Chat, long Bot), Answer> Answers = new();
        private static readonly Dictionary<(long Chat, long Bot), Task<Answer>> Asking = new();

        internal static async Task<bool> IsMemberAsync(long chatId, long botId) => await MembershipAsync(chatId, botId) == true;

        /// <summary>
        /// Whether the bot is in the group: null when Telegram did not answer (kept for <see cref="RetryAfterFailure"/>).
        /// Bot checks (Detection/BotGuard) tell a member from a guest with it, and must not take "no answer" for "no".
        /// </summary>
        internal static async Task<bool?> MembershipAsync(long chatId, long botId)
        {
            if (AdminDirectory.IsKnownAdmin(chatId, botId)) return true;
            return (await AnswerAsync(chatId, botId)).InGroup;
        }

        /// <summary>
        /// Whether the bot is an admin of the group. The admin list (<see cref="AdminDirectory"/>) leaves other bots
        /// out unless a promotion was seen, so this asks Telegram about the bot itself; false when it does not answer.
        /// </summary>
        internal static async Task<bool> IsAdminAsync(long chatId, long botId) =>
            AdminDirectory.IsKnownAdmin(chatId, botId) || (await AnswerAsync(chatId, botId)).IsAdmin;

        private static async Task<Answer> AnswerAsync(long chatId, long botId)
        {
            var key = (chatId, botId);
            Task<Answer> asking;
            lock (Lock)
            {
                if (Answers.TryGetValue(key, out var answer) && answer.Until > Clock.UtcNow) return answer;
                if (!Asking.TryGetValue(key, out asking)) Asking[key] = asking = AskAsync(chatId, botId);
            }
            return await asking;
        }

        /// <summary>Forgets what Telegram said about the bot here: it was promoted, demoted, banned or let back in.</summary>
        internal static void Forget(long chatId, long botId)
        {
            lock (Lock) Answers.Remove((chatId, botId));
        }

        private static async Task<Answer> AskAsync(long chatId, long botId)
        {
            await Task.Yield();
            bool? inGroup;
            var isAdmin = false;
            TimeSpan keep;
            try
            {
                var member = await Bot.Api.GetChatMember(chatId, botId);
                inGroup = member is not ChatMemberLeft and not ChatMemberBanned;
                isAdmin = member is ChatMemberAdministrator or ChatMemberOwner;
                keep = KeepFor;
            }
            catch (Exception e)
            {
                // Mostly 400 "member not found" for a bot that was never in the group: that is an answer.
                var api = Bot.AsApiError(e);
                if (api?.ErrorCode == 400)
                {
                    inGroup = false;
                    keep = KeepFor;
                }
                else
                {
                    ErrorReporter.Report(e, $"Asking whether bot {botId} is in {chatId}");
                    inGroup = null;
                    keep = RetryAfterFailure;
                }
            }
            var result = new Answer(inGroup, isAdmin, Clock.UtcNow + keep);
            lock (Lock)
            {
                if (Answers.Count >= Capacity) Answers.Clear();
                Answers[(chatId, botId)] = result;
                Asking.Remove((chatId, botId));
            }
            return result;
        }

        internal static void ResetForTests()
        {
            lock (Lock)
            {
                Answers.Clear();
                Asking.Clear();
            }
        }
    }
}
