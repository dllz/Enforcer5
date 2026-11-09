using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Detection.Ml;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// <c>similar_to_banned</c> (design sections 3 and 9, build step 15; Daniel, 2026-11-05): a new member's message or image
    /// close to one that got someone removed.
    /// <list type="bullet">
    /// <item><b>What is compared with:</b> the vectors of the messages and files a ban, tempban, kick or delete was about (an
    /// admin's, or the bot's own about abuse), and each group's banned files (<see cref="BannedMedia"/>). A decision about
    /// abuse (spam, scams; the label's category) counts in every group, any other only in its own, so a group's own rules
    /// never make another group's talk look bad. Read from PostgreSQL into memory every <see cref="RefreshEvery"/>, at most
    /// <see cref="MaxReferences"/> of each kind, compared in memory.</item>
    /// <item><b>What is embedded:</b> in the pilot's groups (<see cref="DetectionPilot"/>), new members' texts of at least
    /// <see cref="MinLetters"/> letters and their images, and the message a decision was about (<see cref="Decided"/>). The
    /// vectors are kept (<c>message_embeddings</c>, <c>media_vectors</c>); the text and the image never are. So a model
    /// change starts the references over (Daniel: no text is kept to embed it again).</item>
    /// <item><b>Thresholds</b> from the evals (/workspace/scratch/infinity/eval, 2026-11-04): Harrier 270m flagged 0.1% of
    /// real chat at 0.834; CLIP ViT-B/32 put re-posted variants at 0.70-0.99 and unrelated images at most 0.555.
    /// <see cref="SimilarityTuning"/> holds them; comparisons a little under are near misses.</item>
    /// <item><b>Modes</b> per group and kind (<see cref="AiModes"/>), watch by default: recorded as a trial, nothing done.
    /// Live is offered once the model passes its eval (<see cref="LiveAllowed"/>).</item>
    /// </list>
    /// Best effort: when the model service is down only the vectors are missing.
    /// </summary>
    internal static class SimilarToBanned
    {
        internal const int MinLetters = 20;
        internal const int MaxReferences = 10_000;

        /// <summary>How long a vector behind a decision is kept, anonymous after the group's retention.</summary>
        internal const int ReferenceDays = 730;

        /// <summary>How long an image's vector no decision or banned file points at is kept.</summary>
        internal const int ImageDays = 90;

        internal static TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);

        /// <summary>Whether a group may set a model live: only once it passed its eval with Daniel (<c>MlLive=true</c>).</summary>
        internal static bool LiveAllowed
        {
            get
            {
                if (_liveAllowed is { } set) return set;
                try
                {
                    return string.Equals(RegHelper.GetRegValue("MlLive"), "true", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    // No configuration at all (a test host): not allowed.
                    return false;
                }
            }
            set => _liveAllowed = value;
        }

        private static bool? _liveAllowed;

        /// <summary>An image to embed: the file's id as labels and banned media know it, and what to download for it.</summary>
        internal sealed record PickedImage(string Key, string FileId, string Kind, string MimeType);

        // ------------------------------------------------------------------ which image (pure)

        /// <summary>
        /// The image of <paramref name="message"/> to embed, by the file's id (<see cref="MessageFingerprint.MediaIdOf"/>):
        /// a photo's size closest above 320 pixels (CLIP looks at 224), a static sticker, an animated or video sticker's
        /// preview, an image sent as a file (or its preview when large), and the preview of a GIF, video or round video. Null
        /// for anything else.
        /// </summary>
        internal static PickedImage Pick(Message message)
        {
            if (message == null) return null;
            var key = MessageFingerprint.MediaIdOf(message);
            if (key == null) return null;
            if (message.Photo is { Length: > 0 } sizes)
            {
                var size = sizes.Where(p => Math.Max(p.Width, p.Height) >= 320).OrderBy(p => p.Width * p.Height).FirstOrDefault()
                           ?? sizes.OrderByDescending(p => p.Width * p.Height).First();
                return new PickedImage(key, size.FileId, "photo", "image/jpeg");
            }
            if (message.Sticker is { } sticker)
            {
                if (!sticker.IsAnimated && !sticker.IsVideo) return new PickedImage(key, sticker.FileId, "sticker", "image/webp");
                return sticker.Thumbnail is { } preview ? new PickedImage(key, preview.FileId, "sticker", "image/jpeg") : null;
            }
            if (message.Document is { } document && document.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (document.FileSize is null or <= Embeddings.MaxImageBytes) return new PickedImage(key, document.FileId, "photo", document.MimeType);
                return document.Thumbnail is { } small ? new PickedImage(key, small.FileId, "thumbnail", "image/jpeg") : null;
            }
            var thumbnail = message.Animation?.Thumbnail ?? message.Video?.Thumbnail ?? message.VideoNote?.Thumbnail;
            return thumbnail != null ? new PickedImage(key, thumbnail.FileId, "thumbnail", "image/jpeg") : null;
        }

        /// <summary>The text of a message worth embedding (text or caption of <see cref="MinLetters"/> letters); null for none.</summary>
        internal static string TextOf(Message message)
        {
            var text = message?.Text ?? message?.Caption;
            return MessageFingerprint.Normalize(text).Length >= MinLetters ? text : null;
        }

        /// <summary>A group's mode for texts or images, live only while allowed.</summary>
        private static string Mode(string stored) => stored == AiModes.Live && !LiveAllowed ? AiModes.Watch : stored;

        // ------------------------------------------------------------------ new members' posts

        /// <summary>A group message or edit, on the moderation path. Never waits.</summary>
        internal static void OnMessage(MessageContext ctx)
        {
            try
            {
                if (!Embeddings.Enabled) return;
                var message = ctx.Message;
                if (!ctx.IsGroup || ctx.Watched || message.SenderChat != null || message.From == null || message.From.IsBot) return;
                if (message.NewChatMembers != null || message.LeftChatMember != null) return;
                if (!DetectionPilot.Includes(ctx.ChatId)) return;
                var engine = (ctx.Policy ?? DetectionSettings.For(ctx.ChatId)).Engine;
                var textMode = Mode(engine.TextSimilarity);
                var imageMode = Mode(engine.ImageSimilarity);
                var text = textMode != AiModes.Off ? TextOf(message) : null;
                var image = imageMode != AiModes.Off ? Pick(message) : null;
                if (text == null && image == null) return;
                if (AdminDirectory.IsKnownAdmin(ctx.ChatId, ctx.UserId)) return;
                var chatId = ctx.ChatId;
                var userId = ctx.UserId;
                Bot.Dispatch(() => CheckAsync(chatId, userId, message, text, textMode, image, imageMode), "SimilarToBanned");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Similar to banned in {ctx.ChatId}");
            }
        }

        private static async Task CheckAsync(long chatId, long userId, Message message, string text, string textMode, PickedImage image,
            string imageMode)
        {
            try
            {
                // New members only: nothing of anyone else is sent to the model service.
                if (await MemberTrust.OfAsync(chatId, userId) != TrustLevel.New) return;
                var client = Embeddings.Client;
                if (client == null) return;
                await EnsureLoadedAsync();
                if (text != null)
                {
                    var vector = await Embeddings.AwaitAsync(Embeddings.TextAsync(text));
                    if (vector != null)
                    {
                        await SaveAsync(chatId, userId, message.MessageId, client.TextModel, "newcomer", vector);
                        await CompareAsync(chatId, userId, message, "text", client.TextModel, vector, null, textMode,
                            SimilarityTuning.EmbeddingText, SimilarityTuning.EmbeddingTextNear);
                    }
                }
                // A file banned here is the banned_media sign's, which deletes it; a copy of it is this one's.
                if (image != null && BannedMedia.Contains(chatId, image.Key) != true)
                {
                    var vector = await Embeddings.AwaitAsync(Embeddings.ImageAsync(image.Key, image.FileId, image.Kind, image.MimeType));
                    if (vector != null)
                        await CompareAsync(chatId, userId, message, "image", client.ImageModel, vector, image.Key, imageMode,
                            SimilarityTuning.EmbeddingImage, SimilarityTuning.EmbeddingImageNear);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Similar to banned: the message {message.MessageId} of {userId} in {chatId}");
            }
        }

        /// <summary>The closest reference this group compares with; null when there is none.</summary>
        internal static (EmbeddingReference Reference, double Similarity)? Closest(IReadOnlyList<EmbeddingReference> references,
            long chatId, float[] vector, string ownFile)
        {
            EmbeddingReference best = null;
            var score = double.MinValue;
            foreach (var reference in references)
            {
                if (!reference.Global && reference.ChatId != chatId) continue;
                // The same file banned in this group is the banned_media sign's.
                if (ownFile != null && reference.Ref == ownFile && reference.ChatId == chatId) continue;
                var similarity = Vectors.Cosine(vector, reference.Vector);
                if (similarity > score)
                {
                    score = similarity;
                    best = reference;
                }
            }
            return best == null ? null : (best, score);
        }

        private static async Task CompareAsync(long chatId, long userId, Message message, string kind, string model, float[] vector,
            string ownFile, string mode, double threshold, double near)
        {
            IReadOnlyList<EmbeddingReference> references;
            lock (Lock) references = kind == "text" ? _texts : _images;
            if (Closest(references, chatId, vector, ownFile) is not { } closest) return;
            var (reference, similarity) = closest;
            var here = reference.ChatId == chatId;
            if (similarity < threshold)
            {
                if (similarity >= near)
                    NearMisses.Add(chatId, userId, SignCatalog.SimilarToBanned.Name, similarity, threshold,
                        new { kind, model, here });
                return;
            }
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var percent = Math.Round(similarity * 100).ToString(CultureInfo.InvariantCulture) + "%";
            await DetectionEngine.HitAsync(chatId, userId, message, new SignHit(SignCatalog.SimilarToBanned.Name,
                JsonSerializer.Serialize(new
                {
                    kind, model, similarity = Math.Round(similarity, 3), here, reference = kind == "text" ? reference.Ref : null,
                    ai_mode = mode
                }))
            {
                Similarity = similarity,
                Trial = mode != AiModes.Live,
                Detail = Methods.GetLocaleString(lang, LineKey(kind), percent,
                    Methods.GetLocaleString(lang, here ? "similarHere" : "similarElsewhere"))
            });
        }

        private static string LineKey(string kind) => kind == "image" ? "similarImageLine" : "similarTextLine";

        private static async Task SaveAsync(long chatId, long userId, long messageId, string model, string purpose, float[] vector)
        {
            if (!await Embeddings.StorageAsync()) return;
            var row = new MessageEmbeddingRow(chatId, userId, messageId, model, purpose, vector, new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero));
            HistoryWriter.Enqueue(() => Repositories.Embeddings.SaveMessageAsync(row), $"Saving the vector of the message {messageId} in {chatId}");
        }

        // ------------------------------------------------------------------ decisions

        /// <summary>
        /// A ban, tempban, kick or delete about <paramref name="about"/> (<see cref="Decisions"/>): its text and image are
        /// embedded and kept, so the next one like it is seen. Whether it counts, and where, is the label's (read with the
        /// references). Nothing for a group outside the pilot, or one that turned the kind off. Never waits.
        /// </summary>
        internal static void Decided(long chatId, long userId, Message about)
        {
            try
            {
                if (!Embeddings.Enabled || about == null || chatId > 0 || !DetectionPilot.Includes(chatId)) return;
                var engine = DetectionSettings.For(chatId).Engine;
                var text = Mode(engine.TextSimilarity) != AiModes.Off ? TextOf(about) : null;
                var image = Mode(engine.ImageSimilarity) != AiModes.Off ? Pick(about) : null;
                if (text == null && image == null) return;
                Bot.Dispatch(async () =>
                {
                    try
                    {
                        var client = Embeddings.Client;
                        if (client == null || !await Embeddings.StorageAsync()) return;
                        if (text != null && await Embeddings.AwaitAsync(Embeddings.TextAsync(text)) is { } vector)
                            await SaveAsync(chatId, userId, about.MessageId, client.TextModel, "decision", vector);
                        // Kept by the queue itself (media_vectors), by the file's id.
                        if (image != null) await Embeddings.AwaitAsync(Embeddings.ImageAsync(image.Key, image.FileId, image.Kind, image.MimeType));
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, $"Embedding what the decision about {userId} in {chatId} was about");
                    }
                }, "SimilarToBanned.Decided");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Embedding what the decision about {userId} in {chatId} was about");
            }
        }

        /// <summary>An admin banned the file in <paramref name="message"/> with /block: its image is embedded and kept.</summary>
        internal static void FileBanned(long chatId, Message message)
        {
            try
            {
                if (!Embeddings.Enabled || !DetectionPilot.Includes(chatId)) return;
                if (Mode(DetectionSettings.For(chatId).Engine.ImageSimilarity) == AiModes.Off) return;
                if (Pick(message) is not { } image) return;
                Bot.Dispatch(async () =>
                {
                    if (await Embeddings.StorageAsync())
                        await Embeddings.AwaitAsync(Embeddings.ImageAsync(image.Key, image.FileId, image.Kind, image.MimeType));
                }, "SimilarToBanned.FileBanned");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Embedding a file banned in {chatId}");
            }
        }

        // ------------------------------------------------------------------ the references

        private static readonly object Lock = new();
        private static IReadOnlyList<EmbeddingReference> _texts = Array.Empty<EmbeddingReference>();
        private static IReadOnlyList<EmbeddingReference> _images = Array.Empty<EmbeddingReference>();
        private static DateTime _freshUntil;
        private static Task _loading;

        /// <summary>The text references in memory, for /simtest.</summary>
        internal static IReadOnlyList<EmbeddingReference> TextReferences
        {
            get { lock (Lock) return _texts; }
        }

        /// <summary>The references in memory, for /simtest and tests.</summary>
        internal static (int Texts, int Images) Counts
        {
            get { lock (Lock) return (_texts.Count, _images.Count); }
        }

        /// <summary>
        /// Reads the references when they were never read or are older than <see cref="RefreshEvery"/>; every comparison
        /// waits for the one read (off the message path), so a decision counts within <see cref="RefreshEvery"/>.
        /// </summary>
        internal static Task EnsureLoadedAsync()
        {
            lock (Lock)
            {
                if (_loading != null) return _loading;
                if (_freshUntil > Clock.UtcNow) return Task.CompletedTask;
                _loading = LoadAsync();
                var loading = _loading;
                Bot.Dispatch(() => loading, "SimilarToBanned.Load"); // dispatched work, so the harness waits for it
                return loading;
            }
        }

        private static async Task LoadAsync()
        {
            try
            {
                var client = Embeddings.Client;
                if (client != null && await Embeddings.StorageAsync())
                {
                    var all = await Repositories.Embeddings.ReferencesAsync(client.TextModel, client.ImageModel,
                        new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero) - TimeSpan.FromDays(ReferenceDays), MaxReferences);
                    lock (Lock)
                    {
                        _texts = all.Where(r => r.Kind == "text" && r.Vector is { Length: > 0 }).ToList();
                        _images = all.Where(r => r.Kind == "image" && r.Vector is { Length: > 0 }).ToList();
                    }
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Reading the messages and files that got someone removed");
            }
            finally
            {
                lock (Lock)
                {
                    _freshUntil = Clock.UtcNow + RefreshEvery;
                    _loading = null;
                }
            }
        }

        /// <summary>The next comparison reads the references again (tests, and /simtest after a decision).</summary>
        internal static void Reload()
        {
            lock (Lock) _freshUntil = default;
        }

        internal static void ResetForTests()
        {
            lock (Lock)
            {
                _texts = Array.Empty<EmbeddingReference>();
                _images = Array.Empty<EmbeddingReference>();
                _freshUntil = default;
                _loading = null;
                _liveAllowed = null;
                RefreshEvery = TimeSpan.FromMinutes(10);
            }
        }
    }
}
