using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The kinds of Telegram call, by what they cost and who waits on them. Each has its own
    /// budget and its own 429 pauses, so a group that is flooded with messages never holds up a ban.
    /// </summary>
    internal enum Lane
    {
        /// <summary>Posting and editing messages: paced per chat, per group and bot-wide.</summary>
        Message,
        /// <summary>Bans, kicks, mutes, deletes, chat permissions. Never waits on a message pause.</summary>
        Moderation,
        /// <summary>Answers to button presses and inline queries: someone is waiting on them.</summary>
        Answer,
        /// <summary>Everything else (lookups, polling): passed through.</summary>
        Other
    }

    /// <summary>
    /// A 429 the bot answered itself, without asking Telegram: the chat (or the bot) is paused after
    /// a 429, or the next free slot is too far away to wait for. Callers treat it like Telegram's own.
    /// </summary>
    internal sealed class LocalRateLimitException : ApiRequestException
    {
        internal LocalRateLimitException(int retryAfter)
            : base($"Too Many Requests: retry after {retryAfter} (paused locally)", 429,
                new ResponseParameters { RetryAfter = retryAfter })
        {
        }
    }

    /// <summary>
    /// Keeps the bot under Telegram's limits and honours its 429s. Ported from blackwolf's
    /// FloodControl (Telegram.Bot/Helpers/FloodControl.cs), with lanes so enforcement never waits
    /// behind messages, and with sliding windows instead of token buckets: a bucket of 20 refilling
    /// at 20 a minute still lets 40 through in one minute, which is exactly what earns the 429.
    /// <list type="bullet">
    /// <item>Messages: at most <see cref="ChatMessages"/> per <see cref="ChatWindow"/> per chat,
    /// <see cref="GroupMessages"/> per <see cref="GroupWindow"/> per group, and
    /// <see cref="BotMessages"/> a second bot-wide, below Telegram's 30, so moderation always has
    /// room. A message whose slot is more than <see cref="MessageMaxWait"/> away is refused at once
    /// rather than holding its caller.</item>
    /// <item>Moderation: at most <see cref="BotModeration"/> a second bot-wide, never paced per chat.
    /// After a 429 it is refused at once (the enforcement queue retries it), it never waits.</item>
    /// <item>A 429 pauses that lane in that chat for its <c>retry_after</c>; one on a request without
    /// a chat pauses the lane bot-wide.</item>
    /// </list>
    /// All state is in memory, per process. <see cref="Flooded"/> is where another process would
    /// hear about a 429 (<see cref="IFloodSignal"/>).
    /// </summary>
    internal sealed class FloodControl
    {
        internal int ChatMessages = 3;
        internal TimeSpan ChatWindow = TimeSpan.FromSeconds(3);
        internal int GroupMessages = 20;
        internal TimeSpan GroupWindow = TimeSpan.FromMinutes(1);
        internal int BotMessages = 25;
        internal int BotModeration = 25;
        internal TimeSpan BotWindow = TimeSpan.FromSeconds(1);
        /// <summary>The longest a message waits for its slot or for a pause to end.</summary>
        internal TimeSpan MessageMaxWait = TimeSpan.FromSeconds(10);
        /// <summary>The longest an answer to a button or inline query waits for a pause to end.</summary>
        internal TimeSpan AnswerMaxWait = TimeSpan.FromSeconds(3);
        /// <summary>429s up to this long are waited out and the request sent once more.</summary>
        internal TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

        private readonly object _lock = new();
        private readonly Func<DateTime> _now;
        // Reserved send times per chat and per group, oldest first. Reservations are handed out in
        // order, so each list stays sorted.
        private readonly Dictionary<string, List<DateTime>> _chatSlots = new();
        private readonly Dictionary<string, List<DateTime>> _groupSlots = new();
        private readonly Dictionary<Lane, Queue<DateTime>> _botSends = new();
        private readonly Dictionary<(Lane, string), DateTime> _pausedUntil = new();
        private readonly Dictionary<Lane, DateTime> _lanePausedUntil = new();
        private DateTime _nextPrune;
        private long _floods;

        internal FloodControl(Func<DateTime> now = null)
        {
            _now = now ?? (() => Clock.UtcNow);
        }

        /// <summary>Raised on every 429 from Telegram: lane, method, chat key ("" for none), seconds.</summary>
        internal event Action<Lane, string, string, int> Flooded;

        /// <summary>429s seen since start.</summary>
        internal long FloodCount => System.Threading.Interlocked.Read(ref _floods);

        internal static Lane LaneOf(string method)
        {
            if (string.IsNullOrEmpty(method)) return Lane.Other;
            switch (method)
            {
                case "sendChatAction":
                case "sendInvoice":
                    return Lane.Other;
                case "banChatMember":
                case "unbanChatMember":
                case "restrictChatMember":
                case "setChatPermissions":
                case "deleteMessage":
                case "deleteMessages":
                case "banChatSenderChat":
                case "unbanChatSenderChat":
                case "approveChatJoinRequest":
                case "declineChatJoinRequest":
                    return Lane.Moderation;
                case "answerCallbackQuery":
                case "answerInlineQuery":
                case "answerPreCheckoutQuery":
                case "answerShippingQuery":
                    return Lane.Answer;
            }
            if (method.StartsWith("send", StringComparison.Ordinal) || method.StartsWith("edit", StringComparison.Ordinal) ||
                method.StartsWith("forward", StringComparison.Ordinal) || method.StartsWith("copy", StringComparison.Ordinal))
                return Lane.Message;
            return Lane.Other;
        }

        internal static string ChatKey(ChatId chat) => chat == null ? "" : chat.Identifier?.ToString() ?? chat.Username ?? "";

        /// <summary>Negative ids and @usernames are groups and channels, with the 20-a-minute limit.</summary>
        internal static bool IsGroup(string key) => key.StartsWith("-", StringComparison.Ordinal) || key.StartsWith("@", StringComparison.Ordinal);

        /// <summary>How long the lane is still paused in this chat (or bot-wide); zero when it is not.</summary>
        internal TimeSpan PausedFor(Lane lane, string chatKey)
        {
            lock (_lock) return PausedForLocked(lane, chatKey, _now());
        }

        private TimeSpan PausedForLocked(Lane lane, string chatKey, DateTime now)
        {
            var until = DateTime.MinValue;
            if (_lanePausedUntil.TryGetValue(lane, out var all)) until = all;
            if (chatKey.Length > 0 && _pausedUntil.TryGetValue((lane, chatKey), out var chat) && chat > until) until = chat;
            return until > now ? until - now : TimeSpan.Zero;
        }

        /// <summary>
        /// What a request in <paramref name="lane"/> to <paramref name="chatKey"/> has to do before it
        /// goes out: wait <see cref="Turn.Wait"/>, then ask again (there may be someone ahead), or
        /// give up with a local 429 when <see cref="Turn.Refused"/>.
        /// </summary>
        internal Turn TakeTurn(Lane lane, string chatKey)
        {
            lock (_lock)
            {
                var now = _now();
                PruneIfDue(now);
                var paused = PausedForLocked(lane, chatKey, now);
                switch (lane)
                {
                    case Lane.Moderation:
                        if (paused > TimeSpan.Zero) return Turn.Refuse(paused);
                        return BotSlot(lane, BotModeration, now);
                    case Lane.Answer:
                        if (paused > AnswerMaxWait) return Turn.Refuse(paused);
                        return paused > TimeSpan.Zero ? Turn.WaitFor(paused) : Turn.Go;
                    case Lane.Message:
                        if (paused > MessageMaxWait) return Turn.Refuse(paused);
                        if (paused > TimeSpan.Zero) return Turn.WaitFor(paused);
                        return MessageSlot(chatKey, now);
                    default:
                        return paused > TimeSpan.Zero ? Turn.Refuse(paused) : Turn.Go;
                }
            }
        }

        // Called with the lock held. Reserves the chat's (and group's) next slot; the bot-wide window
        // is checked when that slot comes, since it is shared by every chat and must not queue them
        // behind one another.
        private Turn MessageSlot(string chatKey, DateTime now)
        {
            var chat = Slots(_chatSlots, chatKey, now, ChatWindow);
            var group = IsGroup(chatKey) ? Slots(_groupSlots, chatKey, now, GroupWindow) : null;
            var at = now;
            at = Later(at, NextSlot(chat, ChatMessages, ChatWindow));
            if (group != null) at = Later(at, NextSlot(group, GroupMessages, GroupWindow));
            if (at > now)
            {
                if (at - now > MessageMaxWait) return Turn.Refuse(at - now);
                chat.Add(at);
                group?.Add(at);
                return Turn.Reserved(at - now);
            }
            var bot = BotSlot(Lane.Message, BotMessages, now, take: false);
            if (bot.Wait > TimeSpan.Zero) return bot;
            chat.Add(now);
            group?.Add(now);
            Sends(Lane.Message).Enqueue(now);
            return Turn.Go;
        }

        /// <summary>
        /// For a message whose reserved slot has come: takes the bot-wide slot, or says how long to
        /// wait for it. Never refuses; the bot-wide window frees within a second.
        /// </summary>
        internal TimeSpan TakeReservedBotSlot()
        {
            lock (_lock)
            {
                var now = _now();
                var turn = BotSlot(Lane.Message, BotMessages, now);
                return turn.Wait;
            }
        }

        private Turn BotSlot(Lane lane, int max, DateTime now, bool take = true)
        {
            var sends = Sends(lane);
            while (sends.Count > 0 && sends.Peek() <= now - BotWindow) sends.Dequeue();
            if (sends.Count >= max)
            {
                // The oldest of the last `max` leaves the window first.
                var free = sends.ElementAt(sends.Count - max) + BotWindow - now;
                return Turn.WaitFor(free > TimeSpan.Zero ? free : TimeSpan.FromMilliseconds(1));
            }
            if (take) sends.Enqueue(now);
            return Turn.Go;
        }

        private Queue<DateTime> Sends(Lane lane)
        {
            if (!_botSends.TryGetValue(lane, out var q)) _botSends[lane] = q = new Queue<DateTime>();
            return q;
        }

        private static List<DateTime> Slots(Dictionary<string, List<DateTime>> map, string key, DateTime now, TimeSpan window)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<DateTime>();
            var stale = 0;
            while (stale < list.Count && list[stale] <= now - window) stale++;
            if (stale > 0) list.RemoveRange(0, stale);
            return list;
        }

        // The earliest time a new slot fits: not before the last reservation (first come, first
        // served), and with fewer than `max` in the window that ends there.
        private static DateTime NextSlot(List<DateTime> slots, int max, TimeSpan window)
        {
            if (slots.Count == 0) return DateTime.MinValue;
            var at = slots[slots.Count - 1];
            if (slots.Count >= max) at = Later(at, slots[slots.Count - max] + window);
            return at;
        }

        private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

        /// <summary>Telegram answered 429: pauses the lane in that chat, or bot-wide without a chat.</summary>
        internal void OnFlood(Lane lane, string method, string chatKey, int retryAfterSeconds)
        {
            var seconds = Math.Max(1, retryAfterSeconds);
            lock (_lock)
            {
                var until = _now().AddSeconds(seconds);
                if (chatKey.Length == 0)
                {
                    if (!_lanePausedUntil.TryGetValue(lane, out var current) || current < until) _lanePausedUntil[lane] = until;
                }
                else if (!_pausedUntil.TryGetValue((lane, chatKey), out var current) || current < until)
                {
                    _pausedUntil[(lane, chatKey)] = until;
                }
            }
            System.Threading.Interlocked.Increment(ref _floods);
            try
            {
                Flooded?.Invoke(lane, method, chatKey, seconds);
            }
            catch (Exception e)
            {
                // A listener must never break sending.
                ErrorReporter.Report(e, "Flood listener");
            }
        }

        /// <summary>
        /// Takes a pause another process reported (see <see cref="IFloodSignal"/>), as if this process
        /// had been answered 429 itself. Does not raise <see cref="Flooded"/>.
        /// </summary>
        internal void ImposePause(Lane lane, string chatKey, DateTime untilUtc)
        {
            lock (_lock)
            {
                if (chatKey.Length == 0)
                {
                    if (!_lanePausedUntil.TryGetValue(lane, out var current) || current < untilUtc) _lanePausedUntil[lane] = untilUtc;
                }
                else if (!_pausedUntil.TryGetValue((lane, chatKey), out var current) || current < untilUtc)
                {
                    _pausedUntil[(lane, chatKey)] = untilUtc;
                }
            }
        }

        internal bool ShouldRetry(Lane lane, int retryAfterSeconds) =>
            lane is Lane.Message or Lane.Answer && TimeSpan.FromSeconds(retryAfterSeconds) <= (lane == Lane.Answer ? AnswerMaxWait : MaxRetryAfter);

        private void PruneIfDue(DateTime now)
        {
            if (now < _nextPrune) return;
            _nextPrune = now.AddMinutes(5);
            foreach (var key in _pausedUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList()) _pausedUntil.Remove(key);
            foreach (var key in _chatSlots.Where(p => p.Value.Count == 0 || p.Value[^1] <= now - ChatWindow).Select(p => p.Key).ToList()) _chatSlots.Remove(key);
            foreach (var key in _groupSlots.Where(p => p.Value.Count == 0 || p.Value[^1] <= now - GroupWindow).Select(p => p.Key).ToList()) _groupSlots.Remove(key);
        }

        /// <summary>What <see cref="TakeTurn"/> decided.</summary>
        internal readonly struct Turn
        {
            internal TimeSpan Wait { get; init; }
            internal bool Refused { get; init; }
            /// <summary>A slot is held for this request: when the wait is over it goes, after the bot-wide check.</summary>
            internal bool HasReservation { get; init; }

            internal static Turn Go => default;
            internal static Turn WaitFor(TimeSpan wait) => new Turn { Wait = wait };
            internal static Turn Reserved(TimeSpan wait) => new Turn { Wait = wait, HasReservation = true };
            internal static Turn Refuse(TimeSpan retryIn) => new Turn { Wait = retryIn, Refused = true };

            /// <summary>Whole seconds for a local 429's retry_after, at least one.</summary>
            internal int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(Wait.TotalSeconds));
        }
    }
}
