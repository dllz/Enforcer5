using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Detection.Images;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// NSFW images (Daniel, 2026-11-05), two signs a group turns on in its Detection menu (🔞 NSFW images); both off by default
    /// and only while the models are loaded (<see cref="ImageScans"/>):
    /// <list type="bullet">
    /// <item><c>image_nsfw</c>: a photo, sticker, image file, or the preview of a GIF or video that anyone but an admin or
    /// approved member posts or edits a post into. Deleted and reported; the report's Show button sends it to the admin who
    /// asks, privately and protected (<see cref="DeletedPost"/>).</item>
    /// <item><c>profile_nsfw</c>: a new member's profile photos (every one Telegram gives, up to <see cref="MaxProfilePhotos"/>)
    /// and the images in their personal channel's latest <see cref="ChannelPosts"/> posts, looked at when they join and on
    /// their first message, once a day. Muted and reported by default (the group picks the action); the report shows the
    /// image (<see cref="DeletedPost.Flagged"/>). Each look is kept in <c>profile_scans</c>.</item>
    /// </list>
    /// Stories cannot be looked at: the Bot API has no way to read a user's stories.
    /// /nsfwscan (devs) runs the profile look over a group's members labelled bad and members never labelled, for the evals.
    /// </summary>
    internal static class NsfwSigns
    {
        internal const int MaxProfilePhotos = 100;
        internal const int ChannelPosts = 20;
        internal static readonly TimeSpan OncePer = TimeSpan.FromHours(24);
        private const int RememberedLimit = 100_000;

        /// <summary>The image of a post the models look at.</summary>
        internal sealed record Picked(string UniqueId, string FileId, string Kind);

        // ------------------------------------------------------------------ which image (pure)

        /// <summary>
        /// The image to score in <paramref name="message"/>: a photo's size closest above 640 pixels (the largest if none is),
        /// a static sticker itself and an animated or video sticker's preview, an image sent as a file, and the preview of a
        /// GIF, video or round video. Null for anything else.
        /// </summary>
        internal static Picked Pick(Message message)
        {
            if (message == null) return null;
            if (message.Photo is { Length: > 0 } sizes)
            {
                var size = sizes.Where(p => Math.Max(p.Width, p.Height) >= 640).OrderBy(p => p.Width * p.Height).FirstOrDefault()
                           ?? sizes.OrderByDescending(p => p.Width * p.Height).First();
                return new Picked(size.FileUniqueId, size.FileId, "photo");
            }
            if (message.Sticker is { } sticker)
            {
                if (!sticker.IsAnimated && !sticker.IsVideo) return new Picked(sticker.FileUniqueId, sticker.FileId, "sticker");
                return sticker.Thumbnail is { } preview ? new Picked(preview.FileUniqueId, preview.FileId, "sticker") : null;
            }
            if (message.Document is { } document && document.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true &&
                document.FileSize is null or <= ImageScans.MaxBytes)
                return new Picked(document.FileUniqueId, document.FileId, "photo");
            var thumbnail = message.Animation?.Thumbnail ?? message.Video?.Thumbnail ?? message.VideoNote?.Thumbnail ?? message.Document?.Thumbnail;
            return thumbnail != null ? new Picked(thumbnail.FileUniqueId, thumbnail.FileId, "thumbnail") : null;
        }

        /// <summary>A profile photo's size to score: the smallest at least 640 pixels, else the largest.</summary>
        internal static PhotoSize ProfileSize(PhotoSize[] sizes) =>
            sizes == null || sizes.Length == 0
                ? null
                : sizes.Where(p => Math.Max(p.Width, p.Height) >= 640).OrderBy(p => p.Width * p.Height).FirstOrDefault()
                  ?? sizes.OrderByDescending(p => p.Width * p.Height).First();

        /// <summary>
        /// Why, in the group's language: "97% NSFW: exposed genitals 92%, exposed breasts 64%", or "96% NSFW: no exposed body
        /// parts seen" (most likely swimwear or lingerie: the ViT model cannot tell those from nudity).
        /// </summary>
        internal static string Describe(XDocument lang, NsfwScore score, NsfwThresholds thresholds)
        {
            static string Percent(double v) => Math.Round(v * 100).ToString(CultureInfo.InvariantCulture) + "%";
            var reasons = score.Reasons(thresholds)
                .Select(r => Methods.GetLocaleString(lang, NsfwParts.Keys.TryGetValue(r.Part, out var key) ? key : "nsfwPart_genitals") + " " + Percent(r.Score))
                .Distinct().ToList();
            return Methods.GetLocaleString(lang, "nsfwScoreLine", Percent(score.Nsfw),
                reasons.Count > 0 ? string.Join(", ", reasons) : Methods.GetLocaleString(lang, "nsfwNoParts"));
        }

        private static string Evidence(string kind, NsfwScore score, string model, object extra = null)
        {
            var evidence = new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["nsfw"] = Math.Round(score.Nsfw, 3),
                ["parts"] = score.Parts.ToDictionary(p => p.Key, p => Math.Round(p.Value, 3)),
                ["model"] = model
            };
            if (extra != null) evidence["scan"] = extra;
            return JsonSerializer.Serialize(evidence);
        }

        // ------------------------------------------------------------------ posts

        /// <summary>A group message or edit, on the moderation path: an image is scored in the background. Never waits.</summary>
        internal static void OnMessage(MessageContext ctx)
        {
            try
            {
                if (!ImageScans.Enabled) return;
                var message = ctx.Message;
                if (!ctx.IsGroup || ctx.Watched || message.SenderChat != null || message.From == null || message.From.IsBot) return;
                var policy = ctx.Policy ?? DetectionSettings.For(ctx.ChatId);
                if (!policy.Engine.For(SignCatalog.ImageNsfw.Name).Enabled) return;
                if (AdminDirectory.IsKnownAdmin(ctx.ChatId, ctx.UserId)) return;
                var picked = Pick(message);
                if (picked == null) return;
                var chatId = ctx.ChatId;
                var userId = ctx.UserId;
                Bot.Dispatch(() => ScorePostAsync(chatId, userId, message, picked), "NsfwSigns.Post");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"NSFW check of a post in {ctx.ChatId}");
            }
        }

        private static async Task ScorePostAsync(long chatId, long userId, Message message, Picked picked)
        {
            try
            {
                var score = await ImageScans.AwaitAsync(ImageScans.ScoreAsync(picked.UniqueId, picked.FileId, picked.Kind));
                var thresholds = NsfwThresholds.Current;
                if (score == null || !score.Flagged(thresholds)) return;
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                await DetectionEngine.HitAsync(chatId, userId, message, new SignHit(SignCatalog.ImageNsfw.Name,
                    Evidence(picked.Kind, score, ImageScans.Model?.Version)) { Detail = Describe(lang, score, thresholds) });
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"NSFW check of the post {message.MessageId} in {chatId}");
            }
        }

        // ------------------------------------------------------------------ profiles

        /// <summary>Someone joined: each new person's profile images are looked at, if the group has the check on.</summary>
        internal static void OnJoin(MessageContext ctx)
        {
            if (!ProfileCheckOn(ctx)) return;
            foreach (var joiner in ctx.Message.NewChatMembers ?? Array.Empty<User>())
            {
                if (joiner.IsBot) continue;
                var person = joiner;
                Bot.Dispatch(() => CheckProfileAsync(ctx.ChatId, person, null, "join"), "NsfwSigns.Join");
            }
        }

        /// <summary>A member's first message here: their profile images, if they are new and not looked at today.</summary>
        internal static void OnFirstPost(MessageContext ctx)
        {
            var message = ctx.Message;
            if (!ctx.IsGroup || ctx.Watched || message.SenderChat != null || message.From == null || message.From.IsBot) return;
            if (!ProfileCheckOn(ctx)) return;
            var person = message.From;
            Bot.Dispatch(() => CheckProfileAsync(ctx.ChatId, person, message, "first_post"), "NsfwSigns.FirstPost");
        }

        private static bool ProfileCheckOn(MessageContext ctx) =>
            ImageScans.Enabled && (ctx.Policy ?? DetectionSettings.For(ctx.ChatId)).Engine.For(SignCatalog.ProfileNsfw.Name).Enabled;

        private static async Task CheckProfileAsync(long chatId, User person, Message message, string source)
        {
            try
            {
                if (!await DetectionPilot.IncludesAsync(chatId)) return;
                if (await MemberTrust.OfAsync(chatId, person.Id) != TrustLevel.New) return;
                if (await Methods.IsGroupAdminAsync(person.Id, chatId)) return;
                if (!Claim(chatId, person.Id)) return;
                var scan = await ScanAsync(chatId, person.Id, source);
                if (scan?.Flagged == null) return;
                var thresholds = NsfwThresholds.Current;
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var flagged = scan.Flagged;
                var whereKey = flagged.Kind == "channel_post" ? "nsfwWhere_channel" : "nsfwWhere_profile";
                var where = Methods.GetLocaleString(lang, whereKey);
                // Never the message: the profile is not a post, and the first message is not deleted for it.
                await DetectionEngine.HitAsync(chatId, person.Id, null, new SignHit(SignCatalog.ProfileNsfw.Name,
                    Evidence(flagged.Kind, flagged.Score, scan.Model, new { photos = scan.Photos, channel_posts = scan.ChannelPosts }))
                {
                    Detail = $"{where} · {Describe(lang, flagged.Score, thresholds)}",
                    Shown = DeletedPost.Image(flagged.FileId, null)
                });
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"NSFW check of the profile of {person.Id} in {chatId}");
            }
        }

        /// <summary>An image of a profile that was scored.</summary>
        internal sealed record ScoredImage(string Kind, string UniqueId, string FileId, NsfwScore Score);

        /// <summary>One look at a profile: how many images, the highest scores, and the image that flagged it (null when none did).</summary>
        internal sealed record ProfileScan(int Photos, int ChannelPosts, int Scored, NsfwScore Max, ScoredImage Flagged, string Model);

        /// <summary>
        /// Looks at <paramref name="userId"/>'s profile photos and personal channel and keeps the result (<c>profile_scans</c>).
        /// Null when the models are off or Telegram could not be asked.
        /// </summary>
        /// <param name="chatId">The group it is for; null for none.</param>
        internal static async Task<ProfileScan> ScanAsync(long? chatId, long userId, string source)
        {
            var model = ImageScans.Model;
            if (model == null) return null;
            var images = new List<(string Kind, string UniqueId, string FileId)>();

            var photos = 0;
            for (var offset = 0; offset < MaxProfilePhotos;)
            {
                var start = offset;
                var page = await Profiles.AwaitAsync(Profiles.TelegramAsync(() =>
                    Bot.Api.GetUserProfilePhotos(userId, start, Math.Min(100, MaxProfilePhotos - start))));
                if (page.Failed) return null;
                var found = page.Value?.Photos ?? Array.Empty<PhotoSize[]>();
                foreach (var size in found.Select(ProfileSize).Where(s => s != null)) images.Add(("profile_photo", size.FileUniqueId, size.FileId));
                photos += found.Length;
                offset += found.Length;
                if (found.Length == 0 || offset >= (page.Value?.TotalCount ?? 0)) break;
            }

            var posts = 0;
            var profile = chatId is { } chat ? await Profiles.AwaitAsync(Profiles.UserAsync(chat, userId)) : null;
            if (profile?.PersonalChannelId != null || chatId == null)
            {
                var channel = await Profiles.AwaitAsync(Profiles.TelegramAsync(() => Bot.Api.GetUserPersonalChatMessages(userId, ChannelPosts)));
                foreach (var post in channel.Value ?? Array.Empty<Message>())
                {
                    posts++;
                    if (Pick(post) is { } picked) images.Add(("channel_post", picked.UniqueId, picked.FileId));
                }
            }

            var unique = images.DistinctBy(i => i.UniqueId).ToList();
            var scores = await ImageScans.AwaitAsync(Task.WhenAll(unique.Select(i => ImageScans.ScoreAsync(i.UniqueId, i.FileId, i.Kind))));
            var scored = unique.Zip(scores, (i, s) => s == null ? null : new ScoredImage(i.Kind, i.UniqueId, i.FileId, s))
                .Where(s => s != null).ToList();
            var thresholds = NsfwThresholds.Current;
            var max = NsfwScore.Max(scored.Select(s => s.Score));
            var flagged = scored.Where(s => s.Score.Flagged(thresholds)).OrderByDescending(s => s.Score.Nsfw).FirstOrDefault();
            var scan = new ProfileScan(photos, posts, scored.Count, max, flagged, model.Version);
            if (PostgresStorage.Ready)
                HistoryWriter.Enqueue(() => Repositories.MediaScores.AddScanAsync(new ProfileScanRow(chatId, userId, source, model.Version, photos,
                    posts, scored.Count, max, flagged != null, flagged?.UniqueId, new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero))),
                    $"Saving the profile scan of {userId}");
            return scan;
        }

        // ------------------------------------------------------------------ evals

        /// <summary>
        /// /nsfwscan: looks at the profiles of <paramref name="chatId"/>'s members labelled bad (up to <paramref name="bad"/>) and
        /// of up to <paramref name="others"/> members who posted and never were, one after another, and tells
        /// <paramref name="devId"/> how many each way were flagged. Nothing is done to anyone; every look is kept
        /// (<c>profile_scans</c>, source eval) for the analysis.
        /// </summary>
        internal static async Task EvalAsync(long chatId, long devId, int bad, int others)
        {
            try
            {
                var (badIds, otherIds) = await Repositories.MediaScores.EvalMembersAsync(chatId, bad, others);
                Bot.Send($"Scanning the profiles of {badIds.Count} members labelled bad and {otherIds.Count} others in <code>{chatId}</code>. " +
                         "I will tell you when it is done.", devId);
                var thresholds = NsfwThresholds.Current;
                async Task<(int Scanned, int Flagged, int Photos)> Run(IReadOnlyList<long> ids)
                {
                    int scanned = 0, flagged = 0, withPhotos = 0;
                    foreach (var id in ids)
                    {
                        var scan = await ScanAsync(chatId, id, "eval");
                        if (scan == null) continue;
                        scanned++;
                        if (scan.Photos + scan.ChannelPosts > 0) withPhotos++;
                        if (scan.Flagged != null) flagged++;
                    }
                    return (scanned, flagged, withPhotos);
                }
                var badResult = await Run(badIds);
                var otherResult = await Run(otherIds);
                Bot.Send($"<b>NSFW profile scan</b> of <code>{chatId}</code> (flagged at nsfw {thresholds.Nsfw:0.00})\n" +
                         $"Labelled bad: {badResult.Flagged} flagged of {badResult.Scanned} ({badResult.Photos} with images)\n" +
                         $"Never labelled: {otherResult.Flagged} flagged of {otherResult.Scanned} ({otherResult.Photos} with images)\n" +
                         "Every score is in profile_scans (source eval).", devId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"NSFW profile scan of {chatId}");
            }
        }

        // ------------------------------------------------------------------ once a day

        private static readonly ConcurrentDictionary<(long Chat, long User), DateTime> Looked = new();

        private static bool Claim(long chatId, long userId)
        {
            var now = Clock.UtcNow;
            var claimed = false;
            Looked.AddOrUpdate((chatId, userId), _ => { claimed = true; return now; },
                (_, at) => { if (now - at < OncePer) return at; claimed = true; return now; });
            if (claimed && Looked.Count > RememberedLimit)
            {
                foreach (var old in Looked.Where(a => now - a.Value >= OncePer).Select(a => a.Key).ToList()) Looked.TryRemove(old, out _);
                if (Looked.Count > RememberedLimit) Looked.Clear();
            }
            return claimed;
        }

        internal static void ResetForTests() => Looked.Clear();
    }
}
