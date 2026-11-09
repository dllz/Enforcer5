using System;
using System.Linq;
using Enforcer5.Attributes;
using Enforcer5.Detection;
using Enforcer5.Detection.Images;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static partial class Commands
    {
        /// <summary>
        /// /nsfwscan [group id] [bad] [others], devs only (Daniel, 2026-11-05): runs the NSFW profile look over a group's members
        /// labelled bad (default 100) and members who posted and never were (default 200), in the background, and sends the
        /// dev how many each way were flagged. Nothing is done to anyone; every look is kept in <c>profile_scans</c> (source
        /// eval) for the analysis. In the group itself, or in private with the group's id.
        /// </summary>
        [Command(Trigger = "nsfwscan", DevOnly = true, Description = "Eval: NSFW profile scan of a group's bad and other members")]
        public static void NsfwScan(Update update, string[] args)
        {
            if (!ImageScans.Enabled)
            {
                Bot.SendReply("The NSFW models are not loaded (NsfwModelPath).", update);
                return;
            }
            if (!PostgresStorage.Ready)
            {
                Bot.SendReply("PostgreSQL is not ready.", update);
                return;
            }
            var words = (args.Length > 1 ? args[1] ?? "" : "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var numbers = words.Select(w => long.TryParse(w, out var n) ? n : (long?)null).ToList();
            if (numbers.Any(n => n == null))
            {
                Bot.SendReply("Usage: /nsfwscan [group id] [bad] [others]", update);
                return;
            }
            long chatId;
            var rest = numbers.Select(n => n.Value).ToList();
            if (rest.Count > 0 && rest[0] < 0)
            {
                chatId = rest[0];
                rest.RemoveAt(0);
            }
            else if (update.Message.Chat.Id < 0) chatId = update.Message.Chat.Id;
            else
            {
                Bot.SendReply("Usage: /nsfwscan [group id] [bad] [others] (the group's id in private)", update);
                return;
            }
            var bad = (int)Math.Clamp(rest.ElementAtOrDefault(0) is var b && b > 0 ? b : 100, 1, 1000);
            var others = (int)Math.Clamp(rest.Count > 1 ? rest[1] : 200, 0, 1000);
            if (!NsfwSigns.TryStartEval(chatId, update.Message.From.Id, bad, others))
                Bot.SendReply("A scan is already running; I will tell you when it is done. /queues shows where it is.", update);
        }
    
        /// <summary>
        /// /nsfwtest, devs only (Daniel, 2026-11-05): reply to a photo, sticker, GIF, video or image or video file and the bot scores it as
        /// the NSFW check would (the same image of the post, the same models and thresholds) and answers with the scores and
        /// whether it would be flagged. Nothing is done: no delete, no report, no detection. Works in groups and in private.
        /// </summary>
        [Command(Trigger = "nsfwtest", DevOnly = true, Description = "Reply to an image: its NSFW scores, and whether it would be flagged")]
        public static void NsfwTest(Update update, string[] args)
        {
            if (!ImageScans.Enabled)
            {
                Bot.SendReply("The NSFW models are not loaded (NsfwModelPath).", update);
                return;
            }
            var picked = NsfwSigns.Pick(update.Message.ReplyToMessage);
            var emojiIds = CustomEmojis.IdsOf(update.Message.ReplyToMessage);
            if (picked == null && emojiIds.Count > 0)
            {
                Bot.Dispatch(async () => Bot.SendReply(await NsfwEmojiTestAsync(emojiIds), update), "NsfwTest.Emoji");
                return;
            }
            if (picked == null)
            {
                Bot.SendReply("Reply to a photo, sticker, GIF, video, image file or a message with custom emoji.", update);
                return;
            }
            Bot.Dispatch(async () =>
            {
                var (scored, score) = await NsfwSigns.ScorePickedAsync(picked);
                Bot.SendReply(NsfwTestText(scored, score, NsfwThresholds.Current), update);
            }, "NsfwTest");
        }

        /// <summary>
        /// /nsfwtest on custom emoji (Daniel, 2026-11-09): the first <see cref="EmojiCheck.ScoredPerMessage"/> distinct emoji
        /// are scored as the check scores them, and the answer is the worst, with each pack and whether it is known NSFW.
        /// </summary>
        private static async System.Threading.Tasks.Task<string> NsfwEmojiTestAsync(System.Collections.Generic.IReadOnlyList<string> ids)
        {
            var emoji = await CustomEmojis.ResolveAsync(ids.Take(EmojiCheck.ScoredPerMessage).ToList(), withFiles: true);
            if (emoji.Count == 0) return "Telegram said nothing about these custom emoji.";
            var thresholds = NsfwThresholds.Current;
            (NsfwSigns.Picked Picked, NsfwScore Score)? worst = null;
            var scored = 0;
            foreach (var e in emoji.Values.DistinctBy(e => e.UniqueId))
            {
                if (e.Sticker == null || NsfwSigns.Pick(new Message { Sticker = e.Sticker }) is not { } picked) continue;
                var (at, score) = await NsfwSigns.ScorePickedAsync(picked with { Kind = "custom_emoji" });
                if (score == null) continue;
                scored++;
                if (worst == null || score.Nsfw > worst.Value.Score.Nsfw) worst = (at, score);
            }
            var packs = new System.Collections.Generic.List<string>();
            foreach (var set in emoji.Values.Select(e => e.SetName).Where(n => n != null).Distinct())
            {
                var known = await NsfwEmojiSets.GetAsync(set);
                packs.Add($"<code>{set.FormatHTML()}</code>" + (known == null ? "" : known.Cleared ? " (cleared by a dev)" : " (known NSFW)"));
            }
            var head = $"{emoji.Count} custom emoji, {scored} scored; packs: {(packs.Count == 0 ? "none" : string.Join(", ", packs))}\nThe worst:\n";
            return worst == null ? head + "none could be scored." : head + NsfwTestText(worst.Value.Picked, worst.Value.Score, thresholds);
        }

        /// <summary>
        /// /nsfwemoji &lt;pack&gt; [mark|clear], devs only (Daniel, 2026-11-09): whether a custom emoji pack is known NSFW in every
        /// group, and marks it or clears it. A cleared pack is not banned anywhere for being NSFW again (bans already made in
        /// groups stay; their admins lift them with /unblock).
        /// </summary>
        [Command(Trigger = "nsfwemoji", DevOnly = true, Description = "An emoji pack's NSFW mark: <pack> [mark|clear]")]
        public static void NsfwEmoji(Update update, string[] args)
        {
            var words = (args.Length > 1 ? args[1] ?? "" : "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = words.Length > 0 ? Models.StickerSetName.Parse(words[0]) : null;
            if (name == null)
            {
                Bot.SendReply("Usage: /nsfwemoji &lt;pack name or addemoji link&gt; [mark|clear]", update);
                return;
            }
            var action = words.Length > 1 ? words[1].ToLowerInvariant() : null;
            var dev = update.Message.From.Id;
            Bot.Dispatch(async () =>
            {
                string text;
                switch (action)
                {
                    case "mark":
                        text = await NsfwEmojiSets.MarkAsync(name, null, null, null, "dev")
                            ? $"<code>{name}</code> is marked NSFW: groups with the NSFW check on delete its emoji and ban it."
                            : $"<code>{name}</code> was marked NSFW already.";
                        break;
                    case "clear":
                        text = await NsfwEmojiSets.ClearAsync(name, dev)
                            ? $"<code>{name}</code> is cleared: no group bans it for being NSFW any more. Bans already made stay."
                            : $"<code>{name}</code> was not marked NSFW.";
                        break;
                    default:
                        var set = await NsfwEmojiSets.GetAsync(name);
                        text = set == null ? $"<code>{name}</code> is not marked NSFW."
                            : $"<code>{name}</code>: {(set.Cleared ? "cleared by a dev" : "NSFW")}, marked by {set.Source} on {set.FlaggedAt:yyyy-MM-dd HH:mm} UTC" +
                              (set.Nsfw is { } n ? $", {Math.Round(n * 100)}% by emoji <code>{set.CustomEmojiId}</code>" : "") + ".";
                        break;
                }
                Bot.SendReply(text, update);
            }, "NsfwEmoji");
        }

        /// <summary>The answer to /nsfwtest. Pure, for tests.</summary>
        internal static string NsfwTestText(NsfwSigns.Picked picked, NsfwScore score, NsfwThresholds thresholds)
        {
            static string Percent(double v) => Math.Round(v * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
            if (score == null)
                return $"Could not score this {picked.Kind}: Telegram would not give the file, it is over {ImageScans.MaxBytes / 1024 / 1024} MB, " +
                       $"nothing could be read from it, or it took over {ImageScans.ImageLimit.TotalSeconds:0} s.";
            var parts = score.Parts.OrderByDescending(p => p.Value)
                .Select(p => $"{p.Key.ToLowerInvariant().Replace('_', ' ')} {Percent(p.Value)}" + (p.Value >= thresholds.Part ? " (named)" : ""))
                .ToList();
            // The answer is at the strict level; the nudity-only line says what groups that chose it do.
            return (score.Flagged(thresholds) ? "🔞 <b>Flagged</b>" : "✅ <b>Passes</b>") +
                   $"\nViT: {Percent(score.Nsfw)} NSFW (flags at {Percent(thresholds.Nsfw)})" +
                   $"\nNudeNet: {(parts.Count == 0 ? "no exposed body parts" : string.Join(", ", parts))}" +
                   $" (named in a report from {Percent(thresholds.Part)})" +
                   $"\nNudity only: {(score.Flagged(thresholds with { NudityOnly = true }) ? "🔞 flagged" : "✅ passes")}" +
                   $" (an exposed part at {Percent(NsfwThresholds.NudityPart)} or ViT at {Math.Round(NsfwThresholds.Sure * 100, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}%)" +
                   $"\nScored: the {picked.Kind}{(picked.Kind == "thumbnail" ? " (the preview of a GIF, video or file)" : "")}" +
                   (score.Frames > 1 ? $", {score.Frames} frames, the worst is frame {score.Frame + 1}" : "") +
                   $", <code>{picked.UniqueId.FormatHTML()}</code>";
        }

        /// <summary>
        /// /queues, devs only (2026-11-05): every queue (Helpers/QueueStatus): the background lookups and writes, the
        /// enforcement queue by why its jobs wait, the update queue, the dispatch gates, log channel posts and the buffers;
        /// how much waits, what moves and since when. ⚠️ marks one the watchdog calls stuck.
        /// </summary>
        [Command(Trigger = "queues", DevOnly = true, Description = "Every queue: waiting, moving, stuck")]
        public static void Queues(Update update, string[] args)
        {
            var lines = QueueStatus.Queues.Select(q => q.Read()).Select(q => (q.Stuck ? "⚠️ " : "• ") + q.ToString().FormatHTML());
            var text = "<b>Background queues</b>\n" + string.Join("\n", lines);
            if (NsfwSigns.Scanning) text += "\n\nAn NSFW scan is running.";
            if (ImageScans.TimedOut > 0) text += $"\nImages given up since the start: {ImageScans.TimedOut}";
            Bot.SendReply(text, update);
        }
    }
}
