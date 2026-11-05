using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    /// <item><c>image_nsfw</c>: a photo, sticker, GIF, video, round video, or an image or video sent as a file, that anyone
    /// but an admin or approved member posts or edits a post into. What moves is read as frames, the worst one counting
    /// (Daniel, 2026-11-06: a clean preview let anything through); its preview is looked at first, and alone when the file
    /// is over 20 MB or ffmpeg is missing (<see cref="ScorePickedAsync"/>). Deleted and reported; the report's Show button sends it to the admin who
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

        /// <summary>
        /// The file of a post the models look at, what it is expected to be (<see cref="MediaHint"/>), and its preview, looked
        /// at first, when it has one and is not itself a still image.
        /// </summary>
        internal sealed record Picked(string UniqueId, string FileId, string Kind, MediaHint Hint = MediaHint.Image, Picked Preview = null)
        {
            /// <summary>The one still image of it a look at many posts takes (a personal channel's): the image itself, else its preview.</summary>
            internal Picked Still => Hint == MediaHint.Image ? this with { Preview = null } : Preview;
        }

        // ------------------------------------------------------------------ which image (pure)

        /// <summary>
        /// What to score in <paramref name="message"/> (Daniel, 2026-11-06):
        /// <list type="bullet">
        /// <item>a photo: its size closest above 640 pixels (the largest if none is);</item>
        /// <item>a static sticker itself; a video sticker as a video; an animated (vector) sticker by its preview;</item>
        /// <item>a GIF, video or round video as a video, with its preview;</item>
        /// <item>a file whose name or type says image or video (<see cref="MediaFiles.Classify"/>), or that says nothing about
        /// itself (read to find out), with its preview; any other file by its preview alone, as before.</item>
        /// </list>
        /// A file over <see cref="ImageScans.MaxBytes"/> is judged by its preview. Null when there is nothing to look at.
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
                var preview = Preview(sticker.Thumbnail);
                // Vector (TGS) stickers: a renderer of their own would be needed; their preview stands for them.
                if (sticker.IsAnimated) return preview;
                return Moving(sticker.FileUniqueId, sticker.FileId, sticker.FileSize, "sticker", MediaHint.Video, preview);
            }
            if (message.Animation is { } animation)
                return Moving(animation.FileUniqueId, animation.FileId, animation.FileSize, "animation", MediaHint.Video, Preview(animation.Thumbnail));
            if (message.Video is { } video)
                return Moving(video.FileUniqueId, video.FileId, video.FileSize, "video", MediaHint.Video, Preview(video.Thumbnail));
            if (message.VideoNote is { } note)
                return Moving(note.FileUniqueId, note.FileId, note.FileSize, "video_note", MediaHint.Video, Preview(note.Thumbnail));
            if (message.Document is { } document)
            {
                var preview = Preview(document.Thumbnail);
                if (MediaFiles.Classify(document.MimeType, document.FileName) is not { } hint) return preview;
                // An image file is one image (its own preview adds nothing); a video or a file of unknown kind keeps its preview.
                if (hint == MediaHint.Image && document.FileSize is null or <= ImageScans.MaxBytes)
                    return new Picked(document.FileUniqueId, document.FileId, "file");
                return Moving(document.FileUniqueId, document.FileId, document.FileSize, "file", hint, preview);
            }
            return null;
        }

        private static Picked Preview(PhotoSize thumbnail) =>
            thumbnail == null ? null : new Picked(thumbnail.FileUniqueId, thumbnail.FileId, "thumbnail");

        /// <summary>A file read as frames, with its preview; the preview alone when the file is too large to download.</summary>
        private static Picked Moving(string uniqueId, string fileId, long? size, string kind, MediaHint hint, Picked preview) =>
            size is > ImageScans.MaxBytes || string.IsNullOrEmpty(fileId) ? preview : new Picked(uniqueId, fileId, kind, hint, preview);

        /// <summary>
        /// Scores <paramref name="picked"/>: its preview first, enough when it flags; then the file itself, unless it is a video
        /// and videos cannot be read (no ffmpeg). The answer is what was scored last that has a score: the file's when it
        /// could be read, else the preview's. Null score when neither could be.
        /// </summary>
        internal static async Task<(Picked Scored, NsfwScore Score)> ScorePickedAsync(Picked picked, bool bulk = false)
        {
            NsfwScore preview = null;
            if (picked.Preview is { } first)
            {
                preview = await ImageScans.AwaitAsync(ImageScans.ScoreAsync(first.UniqueId, first.FileId, first.Kind, bulk, first.Hint));
                if (preview != null && preview.Flagged(NsfwThresholds.Current)) return (first, preview);
            }
            if (picked.Hint != MediaHint.Video || ImageScans.Decoder.ReadsVideo)
            {
                var whole = await ImageScans.AwaitAsync(ImageScans.ScoreAsync(picked.UniqueId, picked.FileId, picked.Kind, bulk, picked.Hint));
                if (whole != null) return (picked, whole);
            }
            return preview != null ? (picked.Preview, preview) : (picked, null);
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
            if (score.Frames > 1)
            {
                evidence["frames"] = score.Frames;
                evidence["frame"] = score.Frame;
            }
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
                var (scored, score) = await ScorePickedAsync(picked);
                var thresholds = NsfwThresholds.Current;
                if (score == null || !score.Flagged(thresholds)) return;
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                await DetectionEngine.HitAsync(chatId, userId, message, new SignHit(SignCatalog.ImageNsfw.Name,
                    Evidence(scored.Kind, score, ImageScans.Model?.Version)) { Detail = Describe(lang, score, thresholds) });
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
            var bulk = source == "eval"; // a dev's scan waits behind the groups' images
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
                    // One still a post (the image, or a video's preview): reading every video of a channel would cost too much here.
                    if (Pick(post)?.Still is { } picked) images.Add(("channel_post", picked.UniqueId, picked.FileId));
                }
            }

            var unique = images.DistinctBy(i => i.UniqueId).ToList();
            var scores = await ImageScans.AwaitAsync(Task.WhenAll(unique.Select(i => ImageScans.ScoreAsync(i.UniqueId, i.FileId, i.Kind, bulk))));
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

        /// <summary>How long one member's look may take in a scan before it is skipped.</summary>
        internal static TimeSpan MemberLimit = TimeSpan.FromMinutes(5);

        /// <summary>A progress line to the dev every this many members.</summary>
        internal const int ProgressEvery = 25;

        private static int _scanning;

        /// <summary>A /nsfwscan is running (one at a time).</summary>
        internal static bool Scanning => Volatile.Read(ref _scanning) != 0;

        /// <summary>
        /// /nsfwscan: looks at the profiles of <paramref name="chatId"/>'s members labelled bad (up to <paramref name="bad"/>) and
        /// of up to <paramref name="others"/> members who posted and never were, one after another, and tells
        /// <paramref name="devId"/> how many each way were flagged. Nothing is done to anyone; every look is kept
        /// (<c>profile_scans</c>, source eval) for the analysis. One at a time; a progress line every <see cref="ProgressEvery"/>
        /// members; a member taking over <see cref="MemberLimit"/> is skipped; the dev always gets a last message, also when it
        /// fails (2026-11-05: a scan stuck on one image never said anything again). Its images wait behind the groups'.
        /// False when another scan is running.
        /// </summary>
        internal static bool TryStartEval(long chatId, long devId, int bad, int others)
        {
            if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return false;
            Bot.Dispatch(async () =>
            {
                try
                {
                    await EvalAsync(chatId, devId, bad, others);
                }
                finally
                {
                    Volatile.Write(ref _scanning, 0);
                }
            }, "NsfwScan");
            return true;
        }

        private sealed class EvalCount
        {
            internal int Total, Scanned, Flagged, WithImages, Skipped;

            public override string ToString() =>
                $"{Flagged} flagged of {Scanned} ({WithImages} with images" + (Skipped > 0 ? $", {Skipped} skipped" : "") + ")";
        }

        private static async Task EvalAsync(long chatId, long devId, int bad, int others)
        {
            var badCount = new EvalCount();
            var otherCount = new EvalCount();
            var thresholds = NsfwThresholds.Current;
            string ending = null;
            var started = Clock.UtcNow;
            try
            {
                var (badIds, otherIds) = await Repositories.MediaScores.EvalMembersAsync(chatId, bad, others);
                badCount.Total = badIds.Count;
                otherCount.Total = otherIds.Count;
                Bot.Send($"Scanning the profiles of {badIds.Count} members labelled bad and {otherIds.Count} others in <code>{chatId}</code>. " +
                         $"A line every {ProgressEvery} members; I will tell you when it is done.", devId);
                LogHelper.Info($"NSFW scan of {chatId}: {badIds.Count} bad, {otherIds.Count} others");
                var done = 0;
                async Task Run(IReadOnlyList<long> ids, EvalCount count)
                {
                    foreach (var id in ids)
                    {
                        // Dispatched work of its own: waited for on the clock, so the harness must see it as running.
                        var result = new TaskCompletionSource<ProfileScan>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var member = id;
                        Bot.Dispatch(async () =>
                        {
                            try
                            {
                                result.TrySetResult(await ScanAsync(chatId, member, "eval"));
                            }
                            catch (Exception e)
                            {
                                result.TrySetException(e);
                            }
                        }, "NsfwScan.Member");
                        var scanning = result.Task;
                        if (!await Clock.WaitForAsync(scanning, MemberLimit))
                        {
                            count.Skipped++;
                            LogHelper.Info($"NSFW scan of {chatId}: member {id} took over {MemberLimit.TotalMinutes:0} min, skipped");
                            _ = scanning.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        }
                        else
                        {
                            ProfileScan scan = null;
                            try
                            {
                                scan = await scanning;
                            }
                            catch (Exception e)
                            {
                                ErrorReporter.Report(e, $"NSFW scan of {chatId}: member {id}");
                            }
                            if (scan == null) count.Skipped++;
                            else
                            {
                                count.Scanned++;
                                if (scan.Photos + scan.ChannelPosts > 0) count.WithImages++;
                                if (scan.Flagged != null) count.Flagged++;
                            }
                        }
                        if (++done % ProgressEvery == 0)
                        {
                            var progress = $"{done} of {badIds.Count + otherIds.Count} members: bad {badCount}; others {otherCount}";
                            LogHelper.Info($"NSFW scan of {chatId}: {progress}");
                            Bot.Send($"NSFW scan of <code>{chatId}</code>: {progress}", devId);
                        }
                    }
                }
                await Run(badIds, badCount);
                await Run(otherIds, otherCount);
            }
            catch (Exception e)
            {
                ending = $"Stopped by an error: {e.GetBaseException().Message.FormatHTML()}";
                ErrorReporter.Report(e, $"NSFW profile scan of {chatId}");
            }
            finally
            {
                var text = $"<b>NSFW profile scan</b> of <code>{chatId}</code> (flagged at nsfw {thresholds.Nsfw:0.00}), " +
                           $"{(int)(Clock.UtcNow - started).TotalMinutes} min\n" +
                           $"Labelled bad: {badCount} of {badCount.Total}\n" +
                           $"Never labelled: {otherCount} of {otherCount.Total}\n" +
                           (ending ?? "Every score is in profile_scans (source eval).");
                LogHelper.Info($"NSFW scan of {chatId} ended: bad {badCount}; others {otherCount}{(ending != null ? "; " + ending : "")}");
                try
                {
                    Bot.Send(text, devId);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Sending the end of the NSFW scan of {chatId}");
                }
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

        internal static void ResetForTests()
        {
            Looked.Clear();
            Volatile.Write(ref _scanning, 0);
            MemberLimit = TimeSpan.FromMinutes(5);
        }
    }
}
