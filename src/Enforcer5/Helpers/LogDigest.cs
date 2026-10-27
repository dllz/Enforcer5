using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// A group's log channel entries during a raid, posted together every <see cref="Every"/> instead
    /// of one post per action: a raid of 200 would otherwise be 200 posts, in a channel often shared
    /// by several groups. Nothing is left out; only the "go to message" buttons are, which point at
    /// messages the raid's bans delete anyway. Kept in memory: a restart loses what is waiting, as
    /// with <see cref="Announcements"/>.
    /// </summary>
    internal static class LogDigest
    {
        internal static TimeSpan Every = TimeSpan.FromSeconds(30);
        private const int MaxLines = 300;
        private const int MaxLength = 3800;

        private sealed class Pending
        {
            internal long LogChat;
            internal readonly List<string> Lines = new();
            internal int Dropped;
        }

        private static readonly object Lock = new();
        private static readonly Dictionary<long, Pending> Waiting = new();

        /// <summary>Adds an entry for <paramref name="chatId"/>'s log channel <paramref name="logChat"/>.</summary>
        internal static void Add(long chatId, long logChat, XDocument lang, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            bool schedule;
            lock (Lock)
            {
                schedule = !Waiting.TryGetValue(chatId, out var pending);
                if (schedule) Waiting[chatId] = pending = new Pending();
                pending.LogChat = logChat;
                if (pending.Lines.Count >= MaxLines) pending.Dropped++;
                else pending.Lines.Add(line);
            }
            if (schedule) Bot.Dispatch(() => FlushLaterAsync(chatId), "LogDigest");
        }

        /// <summary>Entries waiting for a chat, for tests.</summary>
        internal static int WaitingFor(long chatId)
        {
            lock (Lock) return Waiting.TryGetValue(chatId, out var p) ? p.Lines.Count : 0;
        }

        private static async Task FlushLaterAsync(long chatId)
        {
            await Clock.Delay(Every);
            Pending pending;
            lock (Lock)
            {
                if (!Waiting.Remove(chatId, out pending)) return;
            }
            var post = new StringBuilder();
            foreach (var line in pending.Lines)
            {
                if (post.Length > 0 && post.Length + line.Length + 2 > MaxLength)
                {
                    Bot.Send(post.ToString(), pending.LogChat);
                    post.Clear();
                }
                if (post.Length > 0) post.Append("\n\n");
                post.Append(line);
            }
            if (pending.Dropped > 0) post.Append($"\n\n(+{pending.Dropped} more)");
            if (post.Length > 0) Bot.Send(post.ToString(), pending.LogChat);
        }

        internal static void ResetForTests()
        {
            lock (Lock) Waiting.Clear();
        }
    }
}
