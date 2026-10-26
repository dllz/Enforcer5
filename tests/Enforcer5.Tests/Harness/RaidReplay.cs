using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Plays a recorded raid (a <see cref="RaidProfile"/>, as /raidprofile exports it) into a simulated
    /// group: every actor becomes a new account, joins come in at their offsets (through an invite
    /// link when the original did), and each message is posted as the same kind of message with the
    /// same content as the others that shared it. What the bot did back then is not replayed; that
    /// is what the replay is for.
    /// </summary>
    internal static class RaidReplay
    {
        /// <summary>Replays <paramref name="profile"/> into <paramref name="chat"/>; returns the accounts that joined.</summary>
        internal static async Task<IReadOnlyList<HarnessUser>> RunAsync(HarnessChat chat, RaidProfile profile, double speed = 1)
        {
            var actors = new Dictionary<string, HarnessUser>();
            var joined = new List<HarnessUser>();
            HarnessUser ActorFor(string actor) =>
                actors.TryGetValue(actor, out var u) ? u : actors[actor] = chat.Add($"Replay {actor}", MemberState.Left);

            var events = profile.Events.Where(e => e.Actor != null && e.Kind is "join" or "message").OrderBy(e => e.T).ToList();
            if (events.Count == 0) return joined;
            var start = events[0].T;
            var elapsed = 0L;
            foreach (var e in events)
            {
                var due = (long)((e.T - start) / speed);
                if (due > elapsed)
                {
                    await chat.AdvanceAsync(TimeSpan.FromMilliseconds(due - elapsed));
                    elapsed = due;
                }
                var user = ActorFor(e.Actor);
                var detail = e.Detail ?? new Dictionary<string, object>();
                if (e.Kind == "join")
                {
                    joined.Add(user);
                    if (Text(detail, "source") == "link")
                        chat.Deliver(chat.MemberJoined(user, $"https://t.me/+{Text(detail, "link")}", "replayed"));
                    else
                        chat.Deliver(chat.Join(user));
                }
                else
                {
                    var content = Text(detail, "content") ?? Guid.NewGuid().ToString("N");
                    var bare = detail.TryGetValue("bare_link", out var b) && b is bool isBare && isBare;
                    if (Text(detail, "type") is "photo" or "gif" or "video" or "sticker" or "document")
                        chat.Deliver(chat.Photo(user, uniqueId: content));
                    else
                        chat.Deliver(chat.Text(user, bare ? $"https://t.me/{content}" : $"replayed message {Letters(content)}"));
                }
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));
            return joined;
        }

        private static string Text(Dictionary<string, object> detail, string key) =>
            detail.TryGetValue(key, out var v) ? v?.ToString() : null;

        // The message hash only looks at letters: turn the content hash into letters, so the same
        // content gives the same hash and different content a different one.
        private static string Letters(string content) => new string(content.Select(c => (char)('a' + c % 26)).ToArray());
    }
}
