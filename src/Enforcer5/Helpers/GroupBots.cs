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

        private sealed record Answer(bool InGroup, DateTime Until);

        private static readonly object Lock = new();
        private static readonly Dictionary<(long Chat, long Bot), Answer> Answers = new();
        private static readonly Dictionary<(long Chat, long Bot), Task<bool>> Asking = new();

        internal static async Task<bool> IsMemberAsync(long chatId, long botId)
        {
            if (AdminDirectory.IsKnownAdmin(chatId, botId)) return true;
            var key = (chatId, botId);
            Task<bool> asking;
            lock (Lock)
            {
                if (Answers.TryGetValue(key, out var answer) && answer.Until > Clock.UtcNow) return answer.InGroup;
                if (!Asking.TryGetValue(key, out asking)) Asking[key] = asking = AskAsync(chatId, botId);
            }
            return await asking;
        }

        private static async Task<bool> AskAsync(long chatId, long botId)
        {
            await Task.Yield();
            bool inGroup;
            TimeSpan keep;
            try
            {
                var member = await Bot.Api.GetChatMember(chatId, botId);
                inGroup = member is not ChatMemberLeft and not ChatMemberBanned;
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
                    inGroup = false;
                    keep = RetryAfterFailure;
                }
            }
            lock (Lock)
            {
                if (Answers.Count >= Capacity) Answers.Clear();
                Answers[(chatId, botId)] = new Answer(inGroup, Clock.UtcNow + keep);
                Asking.Remove((chatId, botId));
            }
            return inGroup;
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
