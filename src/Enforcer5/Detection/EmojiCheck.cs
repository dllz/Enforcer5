using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Detection.Images;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Custom emoji in a group's messages (Daniel, 2026-11-09). Spammers post NSFW images as custom emoji: a message of
    /// nothing but emoji shows them as a grid of large pictures, and the bot sees only text with emoji ids, so none of
    /// the image checks saw them. In order, for a message or edit with custom emoji, in the background:
    /// <list type="number">
    /// <item>An emoji from a pack banned here (/block, or banned below) is deleted (<c>emoji_set</c>), unless an admin or
    /// an approved member posted it; posts made as a channel too, as blocked sticker packs.</item>
    /// <item>Where the NSFW check is on (<c>image_nsfw</c>): an emoji from a pack known NSFW anywhere
    /// (<see cref="NsfwEmojiSets"/>) is deleted and reported, and the pack banned here, without scoring.</item>
    /// <item>Otherwise up to <see cref="ScoredPerMessage"/> of its emoji are scored like stickers (a few packs at a time,
    /// stopping at the first flagged). One flagged: deleted and reported, its pack marked NSFW for every group and banned
    /// here. An emoji is scored once (<c>custom_emoji</c>, <c>media_scores</c>).</item>
    /// </list>
    /// A pack the admins allowed (False report on such a report) is not banned here again for being NSFW, though a
    /// flagged emoji of it is still deleted. Nothing is looked up for a group with no banned emoji pack and the NSFW
    /// check off.
    /// </summary>
    internal static class EmojiCheck
    {
        internal const int ScoredPerMessage = 20;
        private const int ScoredAtOnce = 5;

        /// <summary>A group message or edit, on the moderation path. Never waits.</summary>
        internal static void OnMessage(MessageContext ctx)
        {
            try
            {
                var message = ctx.Message;
                if (!ctx.IsGroup || ctx.Watched || message.IsAutomaticForward) return;
                // An anonymous admin posts as the group itself.
                if (message.SenderChat != null && message.SenderChat.Id == ctx.ChatId) return;
                var ids = CustomEmojis.IdsOf(message);
                if (ids.Count == 0) return;
                var policy = ctx.Policy ?? DetectionSettings.For(ctx.ChatId);
                var nsfw = ImageScans.Enabled && policy.Engine.For(SignCatalog.ImageNsfw.Name).Enabled &&
                           message.SenderChat == null && message.From is { IsBot: false };
                if (!nsfw && !policy.EmojiSets.Any(e => e.Blocks)) return;
                var chatId = ctx.ChatId;
                var userId = ctx.UserId;
                Bot.Dispatch(() => CheckAsync(chatId, userId, message, ids, nsfw), "EmojiCheck");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Custom emoji check of a post in {ctx.ChatId}");
            }
        }

        private static async Task CheckAsync(long chatId, long userId, Message message, IReadOnlyList<string> ids, bool nsfw)
        {
            try
            {
                var emoji = await CustomEmojis.ResolveAsync(ids);
                if (emoji.Count == 0) return;
                var policy = await DetectionSettings.GetAsync(chatId);
                var sets = ids.Where(emoji.ContainsKey).Select(id => emoji[id].SetName).Where(s => s != null).Distinct().ToList();

                var blocked = sets.Where(policy.EmojiSetBlocked).ToList();
                if (blocked.Count > 0)
                {
                    await DeleteBlockedAsync(chatId, userId, message, blocked);
                    return;
                }
                if (!nsfw) return;
                // The engine exempts admins too, but an admin's emoji must not get their pack marked NSFW everywhere.
                if (await Methods.IsGroupAdminAsync(userId, chatId)) return;

                var thresholds = NsfwThresholds.For(policy);
                var known = new List<string>();
                foreach (var set in sets.Where(s => !policy.EmojiSetAllowed(s)))
                    if (Trusted(await NsfwEmojiSets.GetAsync(set), thresholds)) known.Add(set);
                if (known.Count > 0)
                {
                    foreach (var set in known) await EmojiSetBlocks.BlockNsfwAsync(chatId, set);
                    var first = ids.Select(id => emoji.TryGetValue(id, out var e) ? e : null).First(e => e != null && known.Contains(e.SetName));
                    var withFile = (await CustomEmojis.ResolveAsync(new[] { first.Id }, withFiles: true)).GetValueOrDefault(first.Id);
                    var lang = Methods.GetGroupLanguage(chatId).Doc;
                    await DetectionEngine.HitAsync(chatId, userId, message, new SignHit(SignCatalog.ImageNsfw.Name,
                        JsonSerializer.Serialize(new { kind = "custom_emoji", sets = known, known = true }))
                    {
                        Detail = Methods.GetLocaleString(lang, "nsfwEmojiPack", string.Join(", ", known)),
                        Shown = Shown(withFile)
                    });
                    return;
                }

                var flagged = await ScoreAsync(ids, emoji, thresholds);
                if (flagged == null) return;
                var (found, score) = flagged.Value;
                var packBanned = false;
                if (found.SetName != null && !policy.EmojiSetAllowed(found.SetName))
                {
                    await NsfwEmojiSets.MarkAsync(found.SetName, found.Id, (float)score.Nsfw, ImageScans.Model?.Version, "auto");
                    // A pack a dev cleared is not banned for one emoji; that emoji is still deleted.
                    if (await NsfwEmojiSets.IsNsfwAsync(found.SetName))
                    {
                        await EmojiSetBlocks.BlockNsfwAsync(chatId, found.SetName);
                        packBanned = true;
                    }
                }
                var language = Methods.GetGroupLanguage(chatId).Doc;
                var detail = NsfwSigns.Describe(language, score, thresholds);
                if (packBanned) detail = Methods.GetLocaleString(language, "nsfwEmojiPack", found.SetName) + " · " + detail;
                await DetectionEngine.HitAsync(chatId, userId, message, new SignHit(SignCatalog.ImageNsfw.Name,
                    JsonSerializer.Serialize(new
                    {
                        kind = "custom_emoji",
                        sets = found.SetName == null ? Array.Empty<string>() : new[] { found.SetName },
                        emoji = found.Id,
                        nsfw = Math.Round(score.Nsfw, 3),
                        parts = score.Parts.ToDictionary(p => p.Key, p => Math.Round(p.Value, 3)),
                        model = ImageScans.Model?.Version,
                        pack_banned = packBanned
                    }))
                {
                    Detail = detail,
                    Shown = Shown(found)
                });
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Custom emoji check of the post {message.MessageId} in {chatId}");
            }
        }

        /// <summary>
        /// A pack known NSFW is banned here without scoring: at the strict level always; at nudity only (Daniel, 2026-11-09)
        /// only when a dev marked it or the emoji that marked it was sure (<see cref="NsfwThresholds.Sure"/>), since a pack
        /// marked by a strict group may be swimwear. Otherwise its emoji are scored at this group's level.
        /// </summary>
        internal static bool Trusted(NsfwEmojiSet set, NsfwThresholds thresholds) =>
            set is { Cleared: false } && (!thresholds.NudityOnly || set.Source == "dev" || set.Nsfw >= NsfwThresholds.Sure);

        /// <summary>A post with an emoji from a pack banned here: deleted, unless an admin posted it.</summary>
        private static async Task DeleteBlockedAsync(long chatId, long userId, Message message, IReadOnlyList<string> sets)
        {
            var channel = message.SenderChat;
            if (channel == null && await Methods.IsGroupAdminAsync(userId, chatId)) return;
            var evidence = new { sets };
            var deleted = BotRights.MayDelete(chatId) != false;
            if (channel != null)
                DetectionEngine.Checked(chatId, null, message, SignCatalog.EmojiSet, "delete", deleted: deleted, evidence: new { channel = channel.Id, of = evidence });
            else
                DetectionEngine.Checked(chatId, userId, message, SignCatalog.EmojiSet, "delete", deleted: deleted, evidence: evidence);
            // Nothing is said in the group, as for blocked sticker packs.
            await EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, message.MessageId));
        }

        /// <summary>
        /// Scores the message's emoji not yet scored by the current model, at most <see cref="ScoredPerMessage"/>, taking the
        /// packs in turn, <see cref="ScoredAtOnce"/> at a time, and stops at the first flagged. An emoji scored before
        /// counts by its kept score. Null when none is flagged.
        /// </summary>
        private static async Task<(CustomEmojis.Emoji Emoji, NsfwScore Score)?> ScoreAsync(IReadOnlyList<string> ids,
            IReadOnlyDictionary<string, CustomEmojis.Emoji> emoji, NsfwThresholds thresholds)
        {
            var model = ImageScans.Model?.Version;
            if (model == null) return null;
            var inMessage = ids.Where(emoji.ContainsKey).Select(id => emoji[id]).DistinctBy(e => e.UniqueId).ToList();

            // Scored before: by its kept score (the parts are not kept with it; the report says so). At nudity only, a kept
            // score short of sure needs the parts, so it is scored again (media_scores answers that without the model).
            var keptFlags = thresholds.NudityOnly ? NsfwThresholds.Sure : thresholds.Nsfw;
            var before = inMessage.FirstOrDefault(e => e.Model == model && e.Nsfw is { } n && n >= keptFlags);
            if (before != null) return (before, new NsfwScore(before.Nsfw.Value, new Dictionary<string, double>()));

            var toScore = RoundRobin(inMessage.Where(e => e.Model != model || e.Nsfw == null || e.Nsfw >= thresholds.Nsfw))
                .Take(ScoredPerMessage).ToList();
            if (toScore.Count == 0) return null;
            var files = await CustomEmojis.ResolveAsync(toScore.Select(e => e.Id).ToList(), withFiles: true);
            foreach (var batch in toScore.Chunk(ScoredAtOnce))
            {
                var scoring = batch.Select(async e =>
                {
                    var sticker = files.GetValueOrDefault(e.Id)?.Sticker;
                    if (sticker == null || NsfwSigns.Pick(new Message { Sticker = sticker }) is not { } picked) return (e, (NsfwScore)null);
                    var (_, score) = await NsfwSigns.ScorePickedAsync(picked with { Kind = "custom_emoji" }, thresholds: thresholds);
                    if (score != null) await CustomEmojis.ScoredAsync(files[e.Id], (float)score.Nsfw, model);
                    return (e, score);
                }).ToList();
                var scores = await Task.WhenAll(scoring);
                var flagged = scores.Where(s => s.Item2 != null && s.Item2.Flagged(thresholds)).OrderByDescending(s => s.Item2.Nsfw).FirstOrDefault();
                if (flagged.Item2 != null) return (files[flagged.e.Id], flagged.Item2);
            }
            return null;
        }

        /// <summary>
        /// False report on a report about <paramref name="userId"/> with an NSFW sign (Daniel, 2026-11-09): the emoji packs
        /// their detections since <paramref name="since"/> named are allowed in the group: Detection's ban on them is lifted
        /// and they are not banned here again for being NSFW. Other groups keep theirs; an admin's own ban stays.
        /// </summary>
        internal static async Task FalseReportAsync(long chatId, long userId, DateTimeOffset since)
        {
            try
            {
                if (!PostgresStorage.Ready) return;
                await DetectionRecorder.FlushAsync();
                var sets = await Repositories.DetectionLog.NsfwEmojiSetsAsync(chatId, userId, since,
                    new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero).AddMinutes(1));
                foreach (var set in sets) await EmojiSetBlocks.AllowAsync(chatId, set);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Allowing the emoji packs of a false report about {userId} in {chatId}");
            }
        }

        /// <summary>One emoji of each pack in turn, so a message of many packs has each looked at early.</summary>
        internal static IEnumerable<CustomEmojis.Emoji> RoundRobin(IEnumerable<CustomEmojis.Emoji> emoji)
        {
            var queues = emoji.GroupBy(e => e.SetName ?? "").Select(g => new Queue<CustomEmojis.Emoji>(g)).ToList();
            while (queues.Count > 0)
            {
                foreach (var queue in queues) yield return queue.Dequeue();
                queues.RemoveAll(q => q.Count == 0);
            }
        }

        /// <summary>What Show sends for a flagged emoji: the emoji as a sticker, to the admin alone.</summary>
        private static DeletedPost Shown(CustomEmojis.Emoji emoji) =>
            emoji?.Sticker?.FileId is { } file ? new DeletedPost(0, "sticker", file, null, null) : null;
    }
}
