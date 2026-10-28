using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The profile signs (design sections 3 and 7, build step 11), about new members: a link in the bio
    /// (<c>bio_link</c>), an @mention in it (<c>bio_mention</c>), a personal channel (<c>personal_channel</c>), and the
    /// admins' own filter rules on bios and on the channels members point at (their bio, personal channel, forwards,
    /// mentions and t.me links in their messages).
    /// <para>
    /// A member is looked up (<see cref="Profiles"/>) on their first message here, or when they join if the group has a
    /// rule on bios or channels. What was found is applied once a day per member and group, however often it is asked.
    /// Flags only are kept (<see cref="AccountFlags"/>, <see cref="ChannelFlags"/>); never the text.
    /// </para>
    /// </summary>
    internal static class ProfileSigns
    {
        private const int MaxChannels = 3;
        internal static readonly TimeSpan OncePer = TimeSpan.FromHours(24);
        private const int RememberedLimit = 100_000;

        // ------------------------------------------------------------------ what a text holds (pure)

        private static readonly Regex LinkPattern = new Regex(
            @"(https?://[^\s]+|(t\.me|telegram\.me|telegram\.dog)/[^\s]+|www\.[^\s]+|\b[a-z0-9][a-z0-9\-]*\.(com|net|org|io|xyz|me|ru|info|site|online|top|shop|app|link|cc|co|biz|club|live|store|vip|pro|fun|win|bet|gg|to|ly)\b(/[^\s]*)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex MentionPattern = new Regex(@"(^|[^\w@])@([a-z][a-z0-9_]{3,31})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex TelegramLink = new Regex(@"^(https?://)?(www\.)?(t\.me|telegram\.me|telegram\.dog)/([a-z][a-z0-9_]{3,31})(/|\?|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // t.me paths that are not a chat.
        private static readonly HashSet<string> NotChats = new(StringComparer.OrdinalIgnoreCase)
            { "joinchat", "addstickers", "addemoji", "addtheme", "proxy", "socks", "share", "setlanguage", "login", "iv", "boost", "contact", "addlist", "giftcode", "invoice", "confirmphone", "bg", "nft" };

        /// <summary>The links in <paramref name="text"/>: web addresses, t.me links and bare domains.</summary>
        internal static IReadOnlyList<string> Links(string text) =>
            string.IsNullOrEmpty(text) ? Array.Empty<string>() : LinkPattern.Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', '!', '?')).ToList();

        /// <summary>The @usernames in <paramref name="text"/>, lower case without the @.</summary>
        internal static IReadOnlyList<string> Mentions(string text) =>
            string.IsNullOrEmpty(text)
                ? Array.Empty<string>()
                : MentionPattern.Matches(text).Select(m => m.Groups[2].Value.ToLowerInvariant()).Distinct().ToList();

        /// <summary>The public chat a t.me link names (lower case), or null for an invite, a sticker pack or anything else.</summary>
        internal static string TelegramName(string link)
        {
            var match = TelegramLink.Match(link.Trim());
            if (!match.Success) return null;
            var name = match.Groups[4].Value;
            return NotChats.Contains(name) ? null : name.ToLowerInvariant();
        }

        /// <summary>The public chats <paramref name="text"/> names: its @mentions and t.me links.</summary>
        internal static IReadOnlyList<string> ChannelNames(string text) =>
            Mentions(text).Concat(Links(text).Select(TelegramName).Where(n => n != null)).Distinct().ToList();

        /// <summary>What is kept about a member's profile: which of these it has, never what it says.</summary>
        internal static IReadOnlyList<string> AccountFlags(ChatFullInfo info)
        {
            var flags = new List<string>();
            var bio = info.Bio;
            if (!string.IsNullOrEmpty(bio))
            {
                flags.Add("bio");
                AddTextFlags(flags, "bio_", bio);
            }
            if (info.PersonalChat != null) flags.Add("personal_channel");
            if (info.Birthdate != null) flags.Add("birthdate");
            if (info.EmojiStatusCustomEmojiId != null) flags.Add("emoji_status");
            if (info.BusinessLocation != null) flags.Add("business_location");
            if (info.BusinessIntro != null) flags.Add("business_intro");
            if (info.BusinessOpeningHours != null) flags.Add("business_hours");
            if (info.HasPrivateForwards) flags.Add("private_forwards");
            return flags;
        }

        /// <summary>What is kept about a channel or group: which of these its title and description have, never the text.</summary>
        internal static IReadOnlyList<string> ChannelFlags(ChatFullInfo info)
        {
            var flags = new List<string>();
            if (!string.IsNullOrEmpty(info.Description))
            {
                flags.Add("description");
                AddTextFlags(flags, "description_", info.Description);
            }
            var title = $"{info.Title} {info.Username}";
            if (FilterPresets.HasStaffWord(title)) flags.Add("title_staff_word");
            if (FilterPresets.MixesLookalikes(info.Title ?? "")) flags.Add("title_lookalikes");
            if (info.LinkedChatId != null) flags.Add("linked_chat");
            if (info.JoinByRequest) flags.Add("join_by_request");
            if (info.HasProtectedContent) flags.Add("protected_content");
            return flags;
        }

        private static void AddTextFlags(List<string> flags, string prefix, string text)
        {
            if (Links(text).Count > 0) flags.Add(prefix + "link");
            if (Mentions(text).Count > 0) flags.Add(prefix + "mention");
            if (FilterPresets.HasInviteLink(text)) flags.Add(prefix + "invite_link");
            if (FilterPresets.HasPhoneNumber(text)) flags.Add(prefix + "phone");
            if (FilterPresets.HasWallet(text)) flags.Add(prefix + "wallet");
            if (FilterPresets.HasStaffWord(text)) flags.Add(prefix + "staff_word");
            if (FilterPresets.MixesLookalikes(text)) flags.Add(prefix + "lookalikes");
            if (FilterPresets.HasInvisible(text)) flags.Add(prefix + "invisible");
        }

        // ------------------------------------------------------------------ when

        /// <summary>A member's first message here: a new member is looked up.</summary>
        internal static void OnFirstPost(MessageContext ctx)
        {
            var message = ctx.Message;
            if (!ctx.IsGroup || ctx.Watched || message.SenderChat != null || message.From == null || message.From.IsBot) return;
            var person = message.From;
            Bot.Dispatch(() => LookUpAsync(ctx, person, message), "ProfileSigns.FirstPost");
        }

        /// <summary>Someone joined a group with a rule on bios or channels: they are looked up now, not at their first message.</summary>
        internal static void OnJoin(MessageContext ctx)
        {
            var filters = (ctx.Policy ?? DetectionSettings.For(ctx.ChatId)).Filters;
            if (!HasProfileRules(filters)) return;
            foreach (var joiner in ctx.Message.NewChatMembers ?? Array.Empty<User>())
            {
                if (joiner.IsBot) continue;
                var person = joiner;
                Bot.Dispatch(() => LookUpAsync(ctx, person, null), "ProfileSigns.Join");
            }
        }

        /// <summary>An enabled own rule on bios or channels.</summary>
        internal static bool HasProfileRules(FilterSettings filters) =>
            filters != null && filters.OwnRules.Any(r => r.Enabled && r.Target is "bio" or "channel");

        private static async Task LookUpAsync(MessageContext ctx, User person, Message message)
        {
            var chatId = ctx.ChatId;
            try
            {
                if (await MemberTrust.OfAsync(chatId, person.Id) != TrustLevel.New) return;
                if (await Methods.IsGroupAdminAsync(person.Id, chatId)) return;
                var profile = await Profiles.AwaitAsync(Profiles.UserAsync(chatId, person.Id));
                if (profile == null || !Claim(chatId, person.Id, "profile")) return;
                await ApplyProfileAsync(ctx, person, message, profile);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Profile signs of {person.Id} in {chatId}");
            }
        }

        private static async Task ApplyProfileAsync(MessageContext ctx, User person, Message message, ProfileInfo profile)
        {
            var chatId = ctx.ChatId;
            var groupName = ctx.Message.Chat.Username;
            var bio = profile.Bio;
            MemberData.Profile(chatId, person.Id, profile.Flags);
            foreach (var domain in Links(bio).Select(Reputation.DomainOf).Where(d => d != null).Distinct())
                await Reputation.PointedAsync(chatId, person.Id, message, "domain", domain);
            if (!string.IsNullOrEmpty(bio))
            {
                var links = Links(bio).Count(l => !MessageSigns.IsThisGroup(l, groupName, chatId));
                if (links > 0) await Hit(chatId, person.Id, message, SignCatalog.BioLink, new { links });
                var mentions = Mentions(bio).Count(m => !string.Equals(m, groupName, StringComparison.OrdinalIgnoreCase));
                if (mentions > 0) await Hit(chatId, person.Id, message, SignCatalog.BioMention, new { mentions });
                FilterCheck.OnProfile(ctx, person, message, FilterTarget.Bio, bio);
            }

            var linked = await ChatDetails.LinkedChannelAsync(chatId);
            long? personal = profile.PersonalChannelId is { } id && id != linked && id != chatId ? id : null;
            if (personal != null) await Hit(chatId, person.Id, message, SignCatalog.PersonalChannel, new { channel = personal });

            var names = ChannelNames(bio).Where(n => !string.Equals(n, groupName, StringComparison.OrdinalIgnoreCase)
                                                      && !string.Equals(n, person.Username, StringComparison.OrdinalIgnoreCase));
            await PointedAtAsync(ctx, person, message, personal, names);
        }

        /// <summary>
        /// Channels and groups a new member pointed at (their personal channel, a forward, a mention, a link): each looked
        /// up (at most <see cref="MaxChannels"/>, once a day for all groups) and run through the group's channel rules.
        /// </summary>
        internal static async Task PointedAtAsync(MessageContext ctx, User person, Message message, long? channelId, IEnumerable<string> names)
        {
            var chatId = ctx.ChatId;
            try
            {
                var lookups = new List<Task<ChannelInfo>>();
                if (channelId is { } id) lookups.Add(Profiles.ChannelAsync(chatId, id));
                foreach (var name in (names ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (lookups.Count >= MaxChannels) break;
                    lookups.Add(Profiles.ChannelAsync(chatId, name));
                }
                if (lookups.Count == 0) return;
                var channels = await Profiles.AwaitAsync(Task.WhenAll(lookups));
                var linked = await ChatDetails.LinkedChannelAsync(chatId);
                foreach (var channel in channels.Where(c => c != null && c.Id != chatId && c.Id != linked).DistinctBy(c => c.Id))
                {
                    await Reputation.PointedAsync(chatId, person.Id, message, "channel", channel.Id.ToString());
                    if (!Claim(chatId, person.Id, $"channel:{channel.Id}")) continue;
                    FilterCheck.OnProfile(ctx, person, message, FilterTarget.Channel, channel.FilterText);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Channel lookups for {person.Id} in {chatId}");
            }
        }

        private static Task Hit(long chatId, long userId, Message message, SignDefinition sign, object evidence) =>
            DetectionEngine.HitAsync(chatId, userId, message, new SignHit(sign.Name, JsonSerializer.Serialize(evidence)));

        // ------------------------------------------------------------------ once a day

        private static readonly ConcurrentDictionary<(long Chat, long User, string What), DateTime> Applied = new();

        private static bool Claim(long chatId, long userId, string what)
        {
            var now = Clock.UtcNow;
            var claimed = false;
            Applied.AddOrUpdate((chatId, userId, what), _ => { claimed = true; return now; },
                (_, at) => { if (now - at < OncePer) return at; claimed = true; return now; });
            if (claimed && Applied.Count > RememberedLimit)
            {
                foreach (var old in Applied.Where(a => now - a.Value >= OncePer).Select(a => a.Key).ToList()) Applied.TryRemove(old, out _);
                if (Applied.Count > RememberedLimit) Applied.Clear();
            }
            return claimed;
        }

        internal static void ResetForTests() => Applied.Clear();
    }
}
