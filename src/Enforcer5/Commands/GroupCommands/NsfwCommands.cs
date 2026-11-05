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
