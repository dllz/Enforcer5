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
    /// <summary>
    /// Custom emoji in messages (Daniel, 2026-11-09). A message only carries each emoji's id; which pack it is from and what
    /// it looks like takes a getCustomEmojiStickers call. Each emoji is looked up once for every group and both editions:
    /// kept in memory (<see cref="Remembered"/>, with the file ids this bot may download) and in PostgreSQL
    /// (<c>custom_emoji</c>: pack, file and score, never the file id, which differs per bot). Telegram is asked only for
    /// emoji never seen, or for the file of one that still has to be scored, in batches of up to 200, paced with the
    /// other lookups (<see cref="LookupPacer"/>).
    /// </summary>
    internal static class CustomEmojis
    {
        /// <summary>Telegram's limit on one getCustomEmojiStickers call.</summary>
        internal const int PerCall = 200;

        /// <summary>The most distinct emoji of one message looked at; a message has at most 100 custom emoji anyway.</summary>
        internal const int PerMessage = 100;

        internal const int Remembered = 50_000;

        internal const string Static = "static";
        internal const string Video = "video";
        internal const string Animated = "animated";

        /// <summary>
        /// One emoji: its pack (lowercase; null when in none), file and format, its score when it was scored, and the sticker
        /// Telegram gave this process (with the file ids to download it) when it was looked up here.
        /// </summary>
        internal sealed record Emoji(string Id, string SetName, string UniqueId, string Format, float? Nsfw = null, string Model = null,
            Sticker Sticker = null);

        private static readonly object Lock = new();
        private static readonly Dictionary<string, Emoji> Known = new(StringComparer.Ordinal);
        private static readonly Queue<string> KnownOrder = new();
        private static int _calls;

        /// <summary>getCustomEmojiStickers calls made, for tests.</summary>
        internal static int Calls => _calls;

        // ------------------------------------------------------------------ pure

        /// <summary>The distinct custom emoji ids in the text and caption of <paramref name="message"/>, in order, at most <paramref name="max"/>.</summary>
        internal static IReadOnlyList<string> IdsOf(Message message, int max = PerMessage)
        {
            if (message == null) return Array.Empty<string>();
            return (message.Entities ?? Array.Empty<MessageEntity>()).Concat(message.CaptionEntities ?? Array.Empty<MessageEntity>())
                .Where(e => e.Type == MessageEntityType.CustomEmoji && !string.IsNullOrEmpty(e.CustomEmojiId))
                .Select(e => e.CustomEmojiId).Distinct(StringComparer.Ordinal).Take(max).ToList();
        }

        /// <summary>What Telegram's sticker says about the emoji.</summary>
        internal static Emoji From(Sticker sticker) => new(sticker.CustomEmojiId, Models.StickerSetName.Normalise(sticker.SetName),
            sticker.FileUniqueId, sticker.IsVideo ? Video : sticker.IsAnimated ? Animated : Static, Sticker: sticker);

        // ------------------------------------------------------------------ lookups

        /// <summary>
        /// The emoji <paramref name="ids"/>, by id; those Telegram could not tell about are left out. With
        /// <paramref name="withFiles"/>, every one returned has its <see cref="Emoji.Sticker"/> (Telegram is asked for those
        /// known only from PostgreSQL). Never throws: what could not be looked up is missing.
        /// </summary>
        internal static async Task<IReadOnlyDictionary<string, Emoji>> ResolveAsync(IReadOnlyCollection<string> ids, bool withFiles = false)
        {
            var found = new Dictionary<string, Emoji>(StringComparer.Ordinal);
            if (ids == null || ids.Count == 0) return found;
            var missing = new List<string>();
            lock (Lock)
                foreach (var id in ids.Distinct(StringComparer.Ordinal))
                {
                    if (Known.TryGetValue(id, out var known)) found[id] = known;
                    else missing.Add(id);
                }

            if (missing.Count > 0 && PostgresStorage.Ready)
            {
                try
                {
                    foreach (var row in await Repositories.CustomEmoji.GetAsync(missing))
                    {
                        var emoji = new Emoji(row.CustomEmojiId, row.SetName, row.FileUniqueId, row.Format, row.Nsfw, row.Model);
                        found[emoji.Id] = emoji;
                        Remember(emoji);
                    }
                }
                catch (Exception e)
                {
                    // Telegram is asked instead; only costs the calls.
                    ErrorReporter.Report(e, "Reading custom emoji");
                }
            }

            var ask = ids.Distinct(StringComparer.Ordinal)
                .Where(id => !found.TryGetValue(id, out var emoji) || withFiles && emoji.Sticker == null).ToList();
            if (ask.Count == 0) return found;
            var looked = new List<Emoji>();
            foreach (var batch in ask.Chunk(PerCall))
            {
                System.Threading.Interlocked.Increment(ref _calls);
                var answer = await Profiles.AwaitAsync(Profiles.TelegramAsync(() => Bot.Api.GetCustomEmojiStickers(batch)));
                foreach (var sticker in answer.Value ?? Array.Empty<Sticker>())
                {
                    if (string.IsNullOrEmpty(sticker.CustomEmojiId) || string.IsNullOrEmpty(sticker.FileUniqueId)) continue;
                    var emoji = From(sticker);
                    // A score from before stays, as long as it is the same file.
                    if (found.TryGetValue(emoji.Id, out var before) && before.UniqueId == emoji.UniqueId)
                        emoji = emoji with { Nsfw = before.Nsfw, Model = before.Model };
                    else looked.Add(emoji);
                    found[emoji.Id] = emoji;
                    Remember(emoji);
                }
            }
            if (looked.Count > 0 && PostgresStorage.Ready)
            {
                try
                {
                    await Repositories.CustomEmoji.SaveAsync(looked
                        .Select(e => new CustomEmojiRow(e.Id, e.SetName, e.UniqueId, e.Format, null, null)).ToList());
                }
                catch (Exception e)
                {
                    // Looked up again by the other edition or after a restart; only costs the calls.
                    ErrorReporter.Report(e, "Saving custom emoji");
                }
            }
            return found;
        }

        /// <summary>The emoji was scored: kept with it, in memory and in PostgreSQL.</summary>
        internal static async Task ScoredAsync(Emoji emoji, float nsfw, string model)
        {
            Remember(emoji with { Nsfw = nsfw, Model = model });
            if (!PostgresStorage.Ready) return;
            try
            {
                await Repositories.CustomEmoji.SetScoreAsync(emoji.Id, nsfw, model);
            }
            catch (Exception e)
            {
                // Scored again from media_scores next time; only costs a read.
                ErrorReporter.Report(e, $"Saving the score of the custom emoji {emoji.Id}");
            }
        }

        private static void Remember(Emoji emoji)
        {
            lock (Lock)
            {
                // A file id learned here is kept when the same emoji is read again from PostgreSQL without one.
                if (Known.TryGetValue(emoji.Id, out var before))
                {
                    if (emoji.Sticker == null && before.Sticker != null && before.UniqueId == emoji.UniqueId)
                        emoji = emoji with { Sticker = before.Sticker };
                    Known[emoji.Id] = emoji;
                    return;
                }
                Known[emoji.Id] = emoji;
                KnownOrder.Enqueue(emoji.Id);
                while (KnownOrder.Count > Remembered) Known.Remove(KnownOrder.Dequeue());
            }
        }

        internal static void ResetForTests()
        {
            lock (Lock)
            {
                Known.Clear();
                KnownOrder.Clear();
            }
            _calls = 0;
        }
    }
}
