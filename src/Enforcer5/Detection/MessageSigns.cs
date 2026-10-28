using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Raids;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The message signs (design section 3, build step 9), about new members only: a link to another chat
    /// or website, an @mention of a chat outside the group, a forward from a channel or a hidden sender, a
    /// first post that is media, a first post soon after joining, the same content as another new member in
    /// the last day, and joining during a raid. Each adds to the member's score; what that leads to is the
    /// engine's (reports at the group's level, nothing while the group only watches).
    /// <para>
    /// What can be seen on the message is seen inline, without a lookup; everything after that (whether the
    /// sender is new, where a mention points, whether the group's own channel was forwarded) is dispatched
    /// work, so the message handler never waits. The first-post signs come from the message count the stats
    /// already keep (<see cref="OnFirstPost"/>), so they cost one read, on a member's first message only.
    /// </para>
    /// </summary>
    internal static class MessageSigns
    {
        internal static readonly TimeSpan SoonAfterJoining = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan SameWithin = TimeSpan.FromHours(24);

        /// <summary>Text shorter than this (in letters) is not compared: "hello" from two newcomers is no campaign.</summary>
        internal const int SameMinLetters = 20;
        internal const double SameSimilarity = 0.7;
        private const int PerChat = 500;

        /// <summary>A group message from a member, on the moderation path. Never waits.</summary>
        internal static void OnMessage(MessageContext ctx)
        {
            try
            {
                var message = ctx.Message;
                if (ctx.Watched || message.SenderChat != null || message.From == null || message.From.IsBot) return;
                if (message.NewChatMembers != null || message.LeftChatMember != null) return;
                var links = OutsideLinks(message, message.Chat.Username, ctx.ChatId);
                var forward = message.ForwardOrigin is MessageOriginChannel or MessageOriginHiddenUser;
                var mentions = MentionResolver.MentionsIn(message);
                var content = ContentOf(message, links > 0);
                if (links == 0 && !forward && mentions.Count == 0 && content == null) return;
                Bot.Dispatch(() => CheckAsync(ctx, links > 0, forward, mentions, content), "MessageSigns");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Message signs in {ctx.ChatId}");
            }
        }

        private static async Task CheckAsync(MessageContext ctx, bool link, bool forward, IReadOnlyList<Mention> mentions, Content content)
        {
            try
            {
                var chatId = ctx.ChatId;
                var userId = ctx.UserId;
                var message = ctx.Message;
                // New members only: the rest is never looked up, nor remembered for comparing.
                if (await MemberTrust.OfAsync(chatId, userId) != TrustLevel.New) return;

                if (link) await Hit(chatId, userId, message, SignCatalog.LinkOutside, new { links = 1 });
                if (forward && !await FromOwnChannelAsync(chatId, message.ForwardOrigin))
                    await Hit(chatId, userId, message, SignCatalog.ForwardChannel,
                        new { origin = message.ForwardOrigin is MessageOriginChannel c ? c.Chat.Id : (long?)null });
                if (content != null)
                {
                    var others = Remember(chatId, userId, message.MessageId, content);
                    if (others.Count > 0)
                    {
                        await Hit(chatId, userId, message, SignCatalog.SameAsNewcomers, new { others = others.Count });
                        // They posted it too: the same sign for them, about their own message (not here any more).
                        foreach (var other in others)
                            await DetectionEngine.HitAsync(chatId, other, null, new SignHit(SignCatalog.SameAsNewcomers.Name,
                                System.Text.Json.JsonSerializer.Serialize(new { others = others.Count })));
                    }
                }
                if (mentions.Count > 0)
                {
                    var chatUsername = message.Chat.Username;
                    Clock.Park(); // only waits for the lookup queue, which is dispatched work of its own
                    MentionPlace[] places;
                    try { places = await Task.WhenAll(mentions.Select(m => MentionResolver.ResolveAsync(chatId, m, chatUsername))); }
                    finally { Clock.Unpark(); }
                    var outside = places.Count(p => p == MentionPlace.Outside);
                    if (outside > 0) await Hit(chatId, userId, message, SignCatalog.MentionOutside, new { mentions = outside });
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Message signs of {ctx.UserId} in {ctx.ChatId}");
            }
        }

        /// <summary>
        /// A member's first message here (<paramref name="count"/> from the stats is 1): media as a first post,
        /// and a first post soon after joining.
        /// </summary>
        internal static void OnFirstPost(MessageContext ctx)
        {
            var message = ctx.Message;
            if (!ctx.IsGroup || ctx.Watched || message.SenderChat != null || message.From == null || message.From.IsBot) return;
            Bot.Dispatch(async () =>
            {
                try
                {
                    var chatId = ctx.ChatId;
                    var userId = ctx.UserId;
                    if (MessageFingerprint.MediaIdOf(message) != null)
                        await Hit(chatId, userId, message, SignCatalog.FirstPostMedia, new { type = MessageFingerprint.ContentTypeOf(message) });
                    var joined = await Redis.db.HashGetAsync($"{chatId}:users:{userId}", "joined");
                    if (joined.HasValue && long.TryParse(joined.ToString(), out var unix))
                    {
                        var after = message.Date.ToUniversalTime() - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                        if (after >= TimeSpan.Zero && after <= SoonAfterJoining)
                            await Hit(chatId, userId, message, SignCatalog.PostedSoon, new { seconds = (int)after.TotalSeconds });
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"First-post signs of {ctx.UserId} in {ctx.ChatId}");
                }
            }, "FirstPostSigns");
        }

        /// <summary>Everyone who joins while there is a raid alert or raid mode in the group.</summary>
        internal static void OnJoin(MessageContext ctx)
        {
            var chatId = ctx.ChatId;
            if (!RaidGuard.Quiet(chatId)) return;
            foreach (var joiner in ctx.Message.NewChatMembers ?? Array.Empty<User>())
            {
                if (joiner.IsBot) continue;
                var id = joiner.Id;
                Bot.Dispatch(() => Hit(chatId, id, null, SignCatalog.JoinedDuringRaid, null), "JoinedDuringRaid");
            }
        }

        private static Task Hit(long chatId, long userId, Message message, SignDefinition sign, object evidence) =>
            DetectionEngine.HitAsync(chatId, userId, message,
                new SignHit(sign.Name, evidence == null ? null : System.Text.Json.JsonSerializer.Serialize(evidence)));

        // ------------------------------------------------------------------ links and forwards

        /// <summary>Links in the message to another chat or a website; links to this group are not counted. Pure, for tests.</summary>
        internal static int OutsideLinks(Message message, string chatUsername, long chatId)
        {
            var text = message.Text ?? message.Caption ?? "";
            var entities = message.Entities ?? message.CaptionEntities ?? Array.Empty<MessageEntity>();
            var count = 0;
            foreach (var e in entities)
            {
                string url = e.Type switch
                {
                    MessageEntityType.TextLink => e.Url,
                    MessageEntityType.Url when e.Offset >= 0 && e.Offset + e.Length <= text.Length => text.Substring(e.Offset, e.Length),
                    _ => null
                };
                if (url != null && !IsThisGroup(url, chatUsername, chatId)) count++;
            }
            return count;
        }

        private static bool IsThisGroup(string url, string chatUsername, long chatId)
        {
            var link = url.Trim().ToLowerInvariant();
            foreach (var prefix in new[] { "https://", "http://", "www." }) if (link.StartsWith(prefix)) link = link[prefix.Length..];
            foreach (var host in new[] { "t.me/", "telegram.me/", "telegram.dog/" })
            {
                if (!link.StartsWith(host)) continue;
                var path = link[host.Length..];
                if (chatUsername != null && (path == chatUsername.ToLowerInvariant() || path.StartsWith(chatUsername.ToLowerInvariant() + "/")))
                    return true;
                var internalId = chatId.ToString().StartsWith("-100") ? chatId.ToString()[4..] : null;
                return internalId != null && path.StartsWith($"c/{internalId}/");
            }
            return false;
        }

        private static async Task<bool> FromOwnChannelAsync(long chatId, MessageOrigin origin) =>
            origin is MessageOriginChannel channel && channel.Chat.Id == await ChatDetails.LinkedChannelAsync(chatId);

        // ------------------------------------------------------------------ the same content

        internal sealed record Content(string Media, string TextHash, HashSet<int> Sketch);

        private sealed record Posted(long UserId, int MessageId, Content Content, DateTime At);

        private static readonly ConcurrentDictionary<long, List<Posted>> Recent = new();

        /// <summary>What a message is compared by: its file, or a text long enough (or with a link); null for neither.</summary>
        internal static Content ContentOf(Message message, bool hasLink)
        {
            var media = MessageFingerprint.MediaIdOf(message);
            var text = message.Text ?? message.Caption;
            var letters = MessageFingerprint.Normalize(text).Length;
            var textCounts = letters >= SameMinLetters || hasLink && letters > 0;
            if (media == null && !textCounts) return null;
            return new Content(media, textCounts ? MessageFingerprint.Hash(text) : null,
                textCounts && letters >= SameMinLetters ? MessageFingerprint.Sketch(text) : null);
        }

        /// <summary>Remembers what a new member posted; the other new members who posted the same in the last day.</summary>
        internal static IReadOnlyList<long> Remember(long chatId, long userId, int messageId, Content content)
        {
            var now = Clock.UtcNow;
            var list = Recent.GetOrAdd(chatId, _ => new List<Posted>());
            lock (list)
            {
                list.RemoveAll(p => now - p.At >= SameWithin);
                var others = list.Where(p => p.UserId != userId && Same(p.Content, content)).Select(p => p.UserId).Distinct().ToList();
                list.Add(new Posted(userId, messageId, content, now));
                if (list.Count > PerChat) list.RemoveRange(0, list.Count - PerChat);
                return others;
            }
        }

        private static bool Same(Content a, Content b) =>
            a.Media != null && a.Media == b.Media ||
            a.TextHash != null && a.TextHash == b.TextHash ||
            RaidSigns.Similar(a.Sketch, b.Sketch, SameSimilarity);

        internal static void ResetForTests() => Recent.Clear();
    }
}
