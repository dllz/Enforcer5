using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Raids
{
    /// <summary>Where an @mention in a group points.</summary>
    internal enum MentionPlace
    {
        /// <summary>Someone or something of this group: a member, the group itself, its linked channel, @admin.</summary>
        Inside,
        /// <summary>A channel, group or bot elsewhere, someone who is not a member, or a name nobody knows: advertising.</summary>
        Outside
    }

    /// <summary>
    /// One @mention (or a mention of someone without a username, by id) in a message.
    /// <see cref="Username"/> is lower case without the @.
    /// </summary>
    internal sealed record Mention(string Username, long? UserId)
    {
        internal string Key => Username ?? $"#{UserId}";
    }

    /// <summary>
    /// Tells a mention of someone in the group ("@john welcome") from one pointing outside it
    /// ("@cryptogems"): only the latter is a link to raid detection.
    /// <para>
    /// The answer comes from a cache when it can: what is outside is kept for
    /// <see cref="OutsideFor"/>, so during a raid the same advertised name costs nothing after the
    /// first message; what is inside for <see cref="InsideFor"/>. Otherwise the lookup goes into one
    /// queue for the whole bot: the same name asked by many messages is looked up once, Redis first
    /// (<c>bot:usernames</c>, the member's <c>{chat}:users:{id}</c>), Telegram only when Redis does
    /// not know (getChatMember for a known account, getChat for a public chat), at most
    /// <see cref="TelegramPerSecond"/> a second and pausing after a 429. The caller never waits for
    /// it: it gets a task and looks at the message again when it is done.
    /// </para>
    /// </summary>
    internal static class MentionResolver
    {
        internal static TimeSpan OutsideFor = TimeSpan.FromHours(6);
        internal static TimeSpan InsideFor = TimeSpan.FromHours(1);
        /// <summary>When the answer could not be had (a 429, Telegram down): outside for now, asked again after this.</summary>
        internal static TimeSpan FailedFor = TimeSpan.FromMinutes(1);
        internal const int TelegramPerSecond = 10;
        /// <summary>Lookups waiting at most; past that a mention counts as outside without one.</summary>
        internal const int MaxWaiting = 2000;

        private static readonly object Lock = new();
        private static readonly Dictionary<(long Chat, string Key), (MentionPlace Place, DateTime Until)> Cache = new();
        private static readonly Dictionary<(long Chat, string Key), TaskCompletionSource<MentionPlace>> Waiting = new();
        private static readonly Queue<(long Chat, Mention Mention, string ChatUsername)> Queue = new();
        private static bool _draining;
        private static DateTime _pausedUntil;
        private static int _lookups;

        /// <summary>Telegram lookups made, for tests.</summary>
        internal static int TelegramLookups => _lookups;

        /// <summary>The mentions in a message that need an answer: not @admin and the like.</summary>
        internal static IReadOnlyList<Mention> MentionsIn(Message message)
        {
            var text = message.Text ?? message.Caption ?? "";
            var entities = message.Entities ?? message.CaptionEntities ?? Array.Empty<MessageEntity>();
            var found = new List<Mention>();
            foreach (var e in entities)
            {
                if (e.Type == MessageEntityType.TextMention && e.User != null) found.Add(new Mention(null, e.User.Id));
                else if (e.Type == MessageEntityType.Mention && !MessageFingerprint.IsReportMention(text, e) &&
                         e.Offset >= 0 && e.Offset + e.Length <= text.Length && e.Length > 1)
                    found.Add(new Mention(text.Substring(e.Offset + 1, e.Length - 1).ToLowerInvariant(), null));
            }
            return found.DistinctBy(m => m.Key).ToList();
        }

        /// <summary>The answer if it is known now, without a lookup; null otherwise.</summary>
        internal static MentionPlace? Known(long chatId, Mention mention, string chatUsername)
        {
            if (mention.Username != null && chatUsername != null &&
                mention.Username.Equals(chatUsername, StringComparison.OrdinalIgnoreCase))
                return MentionPlace.Inside;
            lock (Lock)
                return Cache.TryGetValue((chatId, mention.Key), out var c) && c.Until > Clock.UtcNow ? c.Place : null;
        }

        /// <summary>
        /// The answer, from the cache or once the queue has looked it up. Never throws; a lookup that
        /// fails answers <see cref="MentionPlace.Outside"/>.
        /// </summary>
        internal static Task<MentionPlace> ResolveAsync(long chatId, Mention mention, string chatUsername)
        {
            if (Known(chatId, mention, chatUsername) is { } known) return Task.FromResult(known);
            bool start;
            TaskCompletionSource<MentionPlace> waiting;
            lock (Lock)
            {
                var key = (chatId, mention.Key);
                if (Waiting.TryGetValue(key, out waiting)) return waiting.Task;
                if (Queue.Count >= MaxWaiting) return Task.FromResult(MentionPlace.Outside);
                waiting = new TaskCompletionSource<MentionPlace>(TaskCreationOptions.RunContinuationsAsynchronously);
                Waiting[key] = waiting;
                Queue.Enqueue((chatId, mention, chatUsername));
                start = !_draining;
                _draining = true;
            }
            // Dispatched work rather than a service loop, so the harness sees the queue as busy.
            if (start) Bot.Dispatch(DrainAsync, "MentionLookups");
            return waiting.Task;
        }

        private static async Task DrainAsync()
        {
            while (true)
            {
                (long Chat, Mention Mention, string ChatUsername) next;
                lock (Lock)
                {
                    if (Queue.Count == 0)
                    {
                        _draining = false;
                        return;
                    }
                    next = Queue.Dequeue();
                }
                MentionPlace place;
                TimeSpan keep;
                try
                {
                    (place, keep) = await LookUpAsync(next.Chat, next.Mention);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Looking up a mention in {next.Chat}");
                    (place, keep) = (MentionPlace.Outside, FailedFor);
                }
                TaskCompletionSource<MentionPlace> waiting;
                lock (Lock)
                {
                    Cache[(next.Chat, next.Mention.Key)] = (place, Clock.UtcNow + keep);
                    Waiting.Remove((next.Chat, next.Mention.Key), out waiting);
                }
                waiting?.TrySetResult(place);
            }
        }

        private static async Task<(MentionPlace, TimeSpan)> LookUpAsync(long chatId, Mention mention)
        {
            var userId = mention.UserId;
            if (userId == null)
            {
                var known = await Redis.db.HashGetAsync("bot:usernames", "@" + mention.Username);
                if (known.HasValue && long.TryParse(known.ToString(), out var id)) userId = id;
            }
            if (userId is { } user)
            {
                // Has been in the group: its own count of them.
                if (await Redis.db.KeyExistsAsync($"{chatId}:users:{user}")) return (MentionPlace.Inside, InsideFor);
                var member = await TelegramAsync(() => Bot.Api.GetChatMember(chatId, user));
                if (member.Failed) return (MentionPlace.Outside, FailedFor);
                return member.Value switch
                {
                    ChatMemberOwner or ChatMemberAdministrator or ChatMemberMember => (MentionPlace.Inside, InsideFor),
                    ChatMemberRestricted r when r.IsMember => (MentionPlace.Inside, InsideFor),
                    _ => (MentionPlace.Outside, OutsideFor)
                };
            }
            // Nobody the bot has seen: a public channel or group, or a name that is no chat at all.
            var chat = await TelegramAsync(() => Bot.Api.GetChat("@" + mention.Username));
            if (chat.NotFound) return (MentionPlace.Outside, OutsideFor);
            if (chat.Failed) return (MentionPlace.Outside, FailedFor);
            if (chat.Value.Id == chatId || chat.Value.Id == await LinkedChannelAsync(chatId)) return (MentionPlace.Inside, InsideFor);
            return (MentionPlace.Outside, OutsideFor);
        }

        private sealed record Answer<T>(T Value, bool Failed, bool NotFound);

        // One Telegram lookup, paced for the whole bot and pausing after a 429.
        private static async Task<Answer<T>> TelegramAsync<T>(Func<Task<T>> call)
        {
            DateTime wait;
            lock (Lock) wait = _pausedUntil;
            if (wait > Clock.UtcNow) await Clock.Delay(wait - Clock.UtcNow);
            System.Threading.Interlocked.Increment(ref _lookups);
            try
            {
                return new Answer<T>(await call(), false, false);
            }
            catch (Exception e)
            {
                var api = Bot.AsApiError(e);
                if (Bot.IsRateLimited(api))
                {
                    var retry = TimeSpan.FromSeconds(api.Parameters?.RetryAfter ?? 5);
                    lock (Lock) _pausedUntil = Clock.UtcNow + retry;
                    return new Answer<T>(default, true, false);
                }
                // "chat not found", "user not found", "PARTICIPANT_ID_INVALID": nothing there by that name.
                if (api != null && api.ErrorCode == 400) return new Answer<T>(default, false, true);
                LogHelper.Error($"Mention lookup failed: {e.Message}");
                return new Answer<T>(default, true, false);
            }
            finally
            {
                // A little over the even spacing, so no second ever holds more than TelegramPerSecond.
                var next = Clock.UtcNow + TimeSpan.FromSeconds(1.05 / TelegramPerSecond);
                lock (Lock)
                    if (_pausedUntil < next) _pausedUntil = next;
            }
        }

        private static Task<long> LinkedChannelAsync(long chatId) => ChatDetails.LinkedChannelAsync(chatId);

        /// <summary>Drops answers that have run out; the raid check calls it every 15 seconds.</summary>
        internal static void Prune()
        {
            var now = Clock.UtcNow;
            lock (Lock)
                foreach (var key in Cache.Where(c => c.Value.Until <= now).Select(c => c.Key).ToList())
                    Cache.Remove(key);
        }

        /// <summary>Forgets everything, as a restart does. For tests.</summary>
        internal static void ResetForTests()
        {
            lock (Lock)
            {
                Cache.Clear();
                foreach (var w in Waiting.Values) w.TrySetResult(MentionPlace.Outside);
                Waiting.Clear();
                Queue.Clear();
                _draining = false;
                _pausedUntil = DateTime.MinValue;
                _lookups = 0;
            }
        }
    }
}
