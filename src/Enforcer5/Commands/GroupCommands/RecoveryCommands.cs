using System;
using System.IO;
using System.Linq;
using System.Text;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Recovery;
using Enforcer5.Storage;
using Telegram.Bot.Types;

namespace Enforcer5
{
    /// <summary>
    /// /recovery and the buttons under data recovery offers. The work is in <see cref="RecoveryOffers"/>.
    /// </summary>
    public static partial class Commands
    {
        /// <summary>
        /// /recovery, devs only, in private. Replying to a <see cref="RecoveryFile"/>: imports it, held. <c>/recovery
        /// preview {chat}</c> sends that group's offer to you as its admins would see it (no buttons);
        /// <c>/recovery release {batch}</c> lets the batch go out; <c>/recovery</c> alone lists the batches.
        /// </summary>
        [Command(Trigger = "recovery", DevOnly = true, Description = "Data recovery offers: import, preview, release")]
        public static void RecoveryCommand(Update update, string[] args)
        {
            var message = update.Message;
            if (!PostgresStorage.Ready)
            {
                Bot.SendReply("PostgreSQL is not ready.", update);
                return;
            }
            var words = (args.Length > 1 ? args[1] ?? "" : "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                if (message.ReplyToMessage?.Document is { } doc)
                {
                    using var content = new MemoryStream();
                    Bot.Files.DownloadAsync(doc.FileId, content).GetAwaiter().GetResult();
                    RecoveryFile.Parsed file;
                    try
                    {
                        file = RecoveryFile.Parse(Encoding.UTF8.GetString(content.ToArray()));
                    }
                    catch (FormatException e)
                    {
                        // Not a bug: the dev is told what is wrong with the file.
                        Bot.SendReply($"Not imported: {e.Message.FormatHTML()}", update);
                        return;
                    }
                    var created = Repositories.Recovery.ImportAsync(file.Batch, file.From, file.To, file.Offers).GetAwaiter().GetResult();
                    Bot.SendReply($"Batch <code>{file.Batch}</code>: {created} new offers of {file.Offers.Count} groups, " +
                                  $"{file.Offers.Sum(o => o.Items.Count)} changes. Held until <code>/recovery release {file.Batch}</code>; " +
                                  "see one with <code>/recovery preview {chat}</code>.", update);
                    return;
                }
                switch (words.FirstOrDefault()?.ToLowerInvariant())
                {
                    case "release" when words.Length == 2:
                        var released = Repositories.Recovery.ReleaseAsync(words[1], new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero)).GetAwaiter().GetResult();
                        Bot.SendReply($"Released {released} offers of <code>{words[1].FormatHTML()}</code>. " +
                                      $"They go out within {RecoveryOffers.Every.TotalMinutes:0} minute(s), from the edition in each group.", update);
                        return;
                    case "preview" when words.Length == 2 && long.TryParse(words[1], out var chat):
                        var pages = RecoveryOffers.PreviewAsync(chat).GetAwaiter().GetResult();
                        if (pages == null) Bot.SendReply("No offer for that group.", update);
                        else foreach (var page in pages) Bot.Send(page, message.From.Id);
                        return;
                    default:
                        var rows = Repositories.Recovery.SummaryAsync().GetAwaiter().GetResult();
                        Bot.SendReply(rows.Count == 0
                            ? "No recovery offers. Reply to a recovery file with /recovery to import it."
                            : string.Join("\n", rows.Select(r => $"<code>{r.Batch.FormatHTML()}</code> {r.Status}: {r.Offers} offers, {r.Items} changes")), update);
                        return;
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Running /recovery");
                Bot.SendReply("Failed; the error went to the error chat.", update);
            }
        }
    }

    public static partial class CallBacks
    {
        /// <summary>A button under a data recovery offer: rcv:{chat}:{offer}:{op}:{n}.</summary>
        [Callback(Trigger = RecoveryOffers.Trigger, GroupAdminOnly = true)]
        public static void RecoveryButton(CallbackQuery call, string[] args)
        {
            if (args.Length < 5 || !long.TryParse(args[1], out var chatId) || !long.TryParse(args[2], out var offerId) ||
                !int.TryParse(args[4], out var n)) return;
            RecoveryOffers.PressAsync(call, chatId, offerId, args[3], n).GetAwaiter().GetResult();
        }
    }
}
