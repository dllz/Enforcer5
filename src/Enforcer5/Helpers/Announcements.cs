using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Group messages that could not go out because the chat is rate limited. Rather than dropping
    /// them, or sleeping until the limit passes, as the send helpers used to, they wait here and go
    /// out as one message once the pause is over ("X was kicked" ... "Y was banned"). A raid's
    /// announcements cost one message instead of one each.
    /// <para>
    /// Only plain group messages are kept: a message with buttons is about one moment and is
    /// dropped, and so is a private message. Kept per process, so a restart loses what is waiting;
    /// they are announcements, the actions themselves are in the enforcement queue.
    /// </para>
    /// </summary>
    internal static class Announcements
    {
        /// <summary>Lines kept per chat; older ones are counted, not sent.</summary>
        internal const int MaxLines = 20;
        private const int MaxLength = 3800;

        private sealed class Pending
        {
            internal readonly List<string> Lines = new();
            internal ParseMode ParseMode;
            internal int Dropped;
        }

        private static readonly object Lock = new();
        private static readonly Dictionary<long, Pending> Waiting = new();

        /// <summary>Messages waiting for a chat, for tests.</summary>
        internal static int WaitingFor(long chatId)
        {
            lock (Lock) return Waiting.TryGetValue(chatId, out var p) ? p.Lines.Count : 0;
        }

        /// <summary>Forgets everything waiting, for tests.</summary>
        internal static void ResetForTests()
        {
            lock (Lock) Waiting.Clear();
        }

        /// <summary>
        /// Keeps <paramref name="text"/> for <paramref name="chatId"/> until its pause is over, or drops
        /// it when it is not a plain group message.
        /// </summary>
        internal static void Defer(long chatId, string text, ParseMode parseMode, InlineKeyboardMarkup menu = null)
        {
            if (chatId >= 0 || menu != null || string.IsNullOrEmpty(text))
            {
                LogHelper.Info($"Rate limited, dropped a message to {chatId}");
                return;
            }
            bool schedule;
            lock (Lock)
            {
                schedule = !Waiting.TryGetValue(chatId, out var pending);
                if (schedule) Waiting[chatId] = pending = new Pending { ParseMode = parseMode };
                if (pending.Lines.Count >= MaxLines)
                {
                    pending.Lines.RemoveAt(0);
                    pending.Dropped++;
                }
                pending.Lines.Add(text);
            }
            if (schedule) Bot.Dispatch(() => FlushLaterAsync(chatId), "Announcements");
        }

        private static async Task FlushLaterAsync(long chatId)
        {
            var paused = Bot.Flood.PausedFor(Lane.Message, chatId.ToString());
            await Clock.Delay(paused + TimeSpan.FromSeconds(1));
            Pending pending;
            lock (Lock)
            {
                if (!Waiting.Remove(chatId, out pending)) return;
            }
            var text = new StringBuilder();
            if (pending.Dropped > 0) text.Append($"(+{pending.Dropped} earlier)\n\n");
            foreach (var line in pending.Lines)
            {
                if (text.Length + line.Length > MaxLength) break;
                if (text.Length > 0) text.Append("\n\n");
                text.Append(line);
            }
            // Through Send, so a chat that is still limited defers it again.
            Bot.Send(text.ToString(), chatId, parseMode: pending.ParseMode);
        }
    }
}
