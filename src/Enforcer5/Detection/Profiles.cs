using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Detection
{
    /// <summary>A member's profile as getChat showed it. The bio is kept in memory only, for a day, and never written anywhere.</summary>
    internal sealed record ProfileInfo(long UserId, string Bio, long? PersonalChannelId, IReadOnlyList<string> Flags, DateTime At);

    /// <summary>
    /// A public channel or group as getChat showed it. Title and description are kept in memory only, for a day, for the
    /// filters; what is written is its ids, username and flags.
    /// </summary>
    internal sealed record ChannelInfo(long Id, string Type, string Username, string Title, string Description, int? MemberCount,
        long? LinkedChatId, IReadOnlyList<string> Flags, DateTime At)
    {
        /// <summary>What a channel filter rule is matched against: title, description and username.</summary>
        internal string FilterText => string.Join("\n", new[] { Title, Description, Username }.Where(t => !string.IsNullOrEmpty(t)));
    }

    /// <summary>
    /// The profile and channel lookups (design section 7, build step 11): getChat on a member for their bio, personal
    /// channel and profile extras, and getChat (with getChatMemberCount) on a public channel or group a member pointed
    /// at. Built like <see cref="Raids.MentionResolver"/>: one queue for the bot, the same thing asked by many is looked
    /// up once, answers kept <see cref="KeepFor"/> in memory and shared by every group, at most <see cref="MaxWaiting"/>
    /// waiting, paced together with the mention lookups (<see cref="LookupPacer"/>). The caller never blocks the message
    /// path: it gets a task.
    /// <para>
    /// While a group has a raid alert or raid mode on (<see cref="Raids.RaidGuard.Quiet"/>), nothing new is looked up for
    /// it. Only flags are written to PostgreSQL (<c>account_profile_flags</c>, <c>channel_profiles</c>); bios, titles
    /// and descriptions never leave memory.
    /// </para>
    /// </summary>
    internal static class Profiles
    {
        internal static TimeSpan KeepFor = TimeSpan.FromHours(24);
        internal static TimeSpan FailedFor = TimeSpan.FromMinutes(1);
        internal const int MaxWaiting = 2000;
        private const int AskedPerChat = 2000;

        private sealed record Key(string Kind, string Value)
        {
            internal static Key User(long id) => new("u", id.ToString());
            internal static Key Chat(long id) => new("c", id.ToString());
            internal static Key Name(string username) => new("@", username.TrimStart('@').ToLowerInvariant());
        }

        private static readonly object Lock = new();
        private static readonly Dictionary<Key, (object Value, DateTime Until)> Cache = new();
        private static readonly Dictionary<Key, TaskCompletionSource<object>> Waiting = new();
        private static readonly Queue<Key> Queue = new();
        private static readonly Dictionary<long, Dictionary<Key, DateTime>> AskedBy = new();
        private static bool _draining;
        private static int _lookups;

        /// <summary>Telegram calls made, for tests.</summary>
        internal static int Lookups => _lookups;

        /// <summary>
        /// <paramref name="userId"/>'s profile, from memory or looked up for <paramref name="chatId"/>; null when it could
        /// not be had (raid in the group, queue full, Telegram refused).
        /// </summary>
        internal static async Task<ProfileInfo> UserAsync(long chatId, long userId) =>
            await GetAsync(chatId, Key.User(userId)) as ProfileInfo;

        /// <summary>A channel or group by id; null when it is private, gone or could not be had.</summary>
        internal static async Task<ChannelInfo> ChannelAsync(long chatId, long channelId) =>
            await GetAsync(chatId, Key.Chat(channelId)) as ChannelInfo;

        /// <summary>A public channel or group by its username; null for none (or a person's username).</summary>
        internal static async Task<ChannelInfo> ChannelAsync(long chatId, string username) =>
            string.IsNullOrWhiteSpace(username) ? null : await GetAsync(chatId, Key.Name(username)) as ChannelInfo;

        private static Task<object> GetAsync(long chatId, Key key)
        {
            bool start;
            TaskCompletionSource<object> waiting;
            lock (Lock)
            {
                Remember(chatId, key);
                if (Cache.TryGetValue(key, out var hit) && hit.Until > Clock.UtcNow) return Task.FromResult(hit.Value);
                if (Waiting.TryGetValue(key, out waiting)) return waiting.Task;
                if (Raids.RaidGuard.Quiet(chatId) || Queue.Count >= MaxWaiting) return Task.FromResult<object>(null);
                waiting = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                Waiting[key] = waiting;
                Queue.Enqueue(key);
                start = !_draining;
                _draining = true;
            }
            // Dispatched work rather than a service loop, so the harness sees the queue as busy.
            if (start) Bot.Dispatch(DrainAsync, "ProfileLookups");
            return waiting.Task;
        }

        /// <summary>
        /// Waits for a lookup from dispatched work: the queue is dispatched work of its own, so the harness counts this
        /// one as waiting meanwhile.
        /// </summary>
        internal static async Task<T> AwaitAsync<T>(Task<T> lookup)
        {
            if (lookup.IsCompleted) return await lookup;
            Clock.Park();
            try { return await lookup; }
            finally { Clock.Unpark(); }
        }

        private static async Task DrainAsync()
        {
            while (true)
            {
                Key next;
                lock (Lock)
                {
                    if (Queue.Count == 0)
                    {
                        _draining = false;
                        return;
                    }
                    next = Queue.Dequeue();
                }
                object value = null;
                var keep = FailedFor;
                (object Value, DateTime Until) known = default;
                bool already;
                // Found meanwhile under another key (a channel asked for by id and by name at once).
                lock (Lock) already = Cache.TryGetValue(next, out known) && known.Until > Clock.UtcNow;
                if (already) (value, keep) = (known.Value, known.Until - Clock.UtcNow);
                else try
                {
                    (value, keep) = next.Kind == "u" ? await LookUpUserAsync(long.Parse(next.Value)) : await LookUpChannelAsync(next);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Looking up {next.Kind}{next.Value}");
                }
                TaskCompletionSource<object> waiting;
                lock (Lock)
                {
                    var until = Clock.UtcNow + keep;
                    Cache[next] = (value, until);
                    // A channel found by its name is known by its id too, and the other way round.
                    if (value is ChannelInfo channel)
                    {
                        Cache[Key.Chat(channel.Id)] = (value, until);
                        if (channel.Username != null) Cache[Key.Name(channel.Username)] = (value, until);
                    }
                    Waiting.Remove(next, out waiting);
                }
                waiting?.TrySetResult(value);
            }
        }

        private static async Task<(object, TimeSpan)> LookUpUserAsync(long userId)
        {
            var answer = await TelegramAsync(() => Bot.Api.GetChat(userId));
            if (answer.Failed) return (null, FailedFor);
            if (answer.Value == null) return (null, KeepFor); // not a chat the bot may see
            var info = answer.Value;
            var profile = new ProfileInfo(userId, info.Bio, info.PersonalChat?.Id, ProfileSigns.AccountFlags(info), Clock.UtcNow);
            Save(() => Repositories.Profiles.SaveAccountAsync(new AccountProfileFlags(userId, profile.Flags, profile.PersonalChannelId,
                MessageFingerprint.ScriptOf(info.Bio), Offset(profile.At))), $"Saving the profile flags of {userId}");
            return (profile, KeepFor);
        }

        private static async Task<(object, TimeSpan)> LookUpChannelAsync(Key key)
        {
            ChatId target = key.Kind == "@" ? new ChatId("@" + key.Value) : new ChatId(long.Parse(key.Value));
            var answer = await TelegramAsync(() => Bot.Api.GetChat(target));
            if (answer.Failed) return (null, FailedFor);
            // Not found, private, or a person rather than a chat.
            if (answer.Value == null || answer.Value.Type == ChatType.Private) return (null, KeepFor);
            var info = answer.Value;
            var count = await TelegramAsync(() => Bot.Api.GetChatMemberCount(info.Id));
            var channel = Channel(info, count.Failed ? null : count.Value);
            SaveChannel(channel);
            return (channel, KeepFor);
        }

        /// <summary>
        /// A public channel or group someone else just read with getChat (the mention lookups): kept and saved as if
        /// looked up here, without its member count, so the same advertised name costs one call, not two.
        /// </summary>
        internal static void Seen(ChatFullInfo info)
        {
            if (info == null || info.Type == ChatType.Private) return;
            var key = Key.Chat(info.Id);
            lock (Lock)
                if (Cache.TryGetValue(key, out var hit) && hit.Until > Clock.UtcNow && hit.Value != null) return;
            var channel = Channel(info, null);
            lock (Lock)
            {
                var until = Clock.UtcNow + KeepFor;
                Cache[key] = (channel, until);
                if (channel.Username != null) Cache[Key.Name(channel.Username)] = (channel, until);
            }
            SaveChannel(channel);
        }

        private static ChannelInfo Channel(ChatFullInfo info, int? members) =>
            new ChannelInfo(info.Id, Type(info.Type), info.Username, info.Title, info.Description, members, info.LinkedChatId,
                ProfileSigns.ChannelFlags(info), Clock.UtcNow);

        private static void SaveChannel(ChannelInfo channel) =>
            Save(() => Repositories.Profiles.SaveChannelAsync(new ChannelProfileRow(channel.Id, channel.Type, channel.Username, channel.Flags,
                channel.MemberCount, channel.LinkedChatId, Offset(channel.At))), $"Saving the profile of channel {channel.Id}");

        private static string Type(ChatType type) => type switch
        {
            ChatType.Channel => "channel",
            ChatType.Group => "group",
            ChatType.Supergroup => "supergroup",
            _ => "private"
        };

        private static DateTimeOffset Offset(DateTime utc) =>
            DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds());

        private static void Save(Func<Task> write, string what)
        {
            if (!PostgresStorage.Ready) return;
            Bot.Dispatch(async () =>
            {
                try { await write(); }
                catch (Exception e) { ErrorReporter.Report(e, what); }
            }, "Profiles.Save");
        }

        private sealed record Answer<T>(T Value, bool Failed);

        // One Telegram call, paced with the mention lookups; 400 (not found, not allowed) is an answer: nothing.
        private static async Task<Answer<T>> TelegramAsync<T>(Func<Task<T>> call)
        {
            await LookupPacer.TurnAsync();
            System.Threading.Interlocked.Increment(ref _lookups);
            try
            {
                return new Answer<T>(await call(), false);
            }
            catch (Exception e)
            {
                var api = Bot.AsApiError(e);
                if (Bot.IsRateLimited(api))
                {
                    LookupPacer.Pause(TimeSpan.FromSeconds(api.Parameters?.RetryAfter ?? 5));
                    return new Answer<T>(default, true);
                }
                if (api != null && api.ErrorCode is 400 or 403) return new Answer<T>(default, false);
                LogHelper.Error($"Profile lookup failed: {e.Message}");
                return new Answer<T>(default, true);
            }
        }

        // ------------------------------------------------------------------ for /filter test

        private static void Remember(long chatId, Key key)
        {
            if (!AskedBy.TryGetValue(chatId, out var asked)) AskedBy[chatId] = asked = new Dictionary<Key, DateTime>();
            asked[key] = Clock.UtcNow;
            if (asked.Count > AskedPerChat)
                foreach (var old in asked.OrderBy(a => a.Value).Take(asked.Count - AskedPerChat).Select(a => a.Key).ToList())
                    asked.Remove(old);
        }

        /// <summary>The bios of the members looked up for <paramref name="chatId"/> in the last day, with who they are.</summary>
        internal static IReadOnlyList<ProfileInfo> RecentBios(long chatId) => Recent<ProfileInfo>(chatId).Where(p => p.Bio != null).ToList();

        /// <summary>The channels and groups members of <paramref name="chatId"/> pointed at in the last day.</summary>
        internal static IReadOnlyList<ChannelInfo> RecentChannels(long chatId) => Recent<ChannelInfo>(chatId).DistinctBy(c => c.Id).ToList();

        private static List<T> Recent<T>(long chatId) where T : class
        {
            var now = Clock.UtcNow;
            lock (Lock)
            {
                if (!AskedBy.TryGetValue(chatId, out var asked)) return new List<T>();
                return asked.Where(a => now - a.Value < KeepFor)
                    .Select(a => Cache.TryGetValue(a.Key, out var hit) && hit.Until > now ? hit.Value as T : null)
                    .Where(v => v != null).ToList();
            }
        }

        /// <summary>Drops answers that have run out; the raid check calls it.</summary>
        internal static void Prune()
        {
            var now = Clock.UtcNow;
            lock (Lock)
            {
                foreach (var key in Cache.Where(c => c.Value.Until <= now).Select(c => c.Key).ToList()) Cache.Remove(key);
                foreach (var (chat, asked) in AskedBy.ToList())
                {
                    foreach (var key in asked.Where(a => now - a.Value >= KeepFor).Select(a => a.Key).ToList()) asked.Remove(key);
                    if (asked.Count == 0) AskedBy.Remove(chat);
                }
            }
        }

        internal static void ResetForTests()
        {
            lock (Lock)
            {
                Cache.Clear();
                foreach (var w in Waiting.Values) w.TrySetResult(null);
                Waiting.Clear();
                Queue.Clear();
                AskedBy.Clear();
                _draining = false;
                _lookups = 0;
            }
        }
    }
}
