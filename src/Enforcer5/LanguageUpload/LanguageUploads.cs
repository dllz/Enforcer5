using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using File = System.IO.File;

namespace Enforcer5.LanguageUpload
{
    /// <summary>
    /// Language uploads in Telegram, ported from blackwolf. /uploadlanguage on an .xml or a .zip checks every file
    /// and posts one message in the chat; a language admin taps Review and decides in a menu only they see (an
    /// ephemeral message), and that one message then says what went live. In a private chat the menu is an ordinary
    /// message, since ephemeral messages are for groups only. Ephemeral messages can vanish, so the state is on disk
    /// (<see cref="BatchStore"/>) and Review opens the menu again where it was.
    /// </summary>
    internal static class LanguageUploads
    {
        /// <summary>The live language folder; a test points it elsewhere.</summary>
        internal static Func<string> LiveDirectory = () => Bot.LanguageDirectory;

        /// <summary>This edition's upload staging; a test points it elsewhere.</summary>
        internal static Func<string> StagingDirectory = () => Bot.TempLanguageDirectory;

        private static BatchStore Store => new BatchStore(Path.Combine(StagingDirectory(), "batches"));

        private const string Expired = "This upload has expired. Upload the files again.";

        /// <summary>Who may review: language admins and devs.</summary>
        internal static bool MayReview(long userId) => Constants.Devs.Contains(userId) || Methods.IsLangAdmin(userId);

        /// <summary>/uploadlanguage: <paramref name="command"/> replies to the uploaded document.</summary>
        internal static async Task StartAsync(Message command)
        {
            var chatId = command.Chat.Id;
            var doc = command.ReplyToMessage?.Document;
            if (doc == null || !(BatchIntake.IsXml(doc.FileName) || BatchIntake.IsZip(doc.FileName)))
            {
                Bot.SendReply("Reply to an .xml file, or a .zip of them, with /uploadlanguage", command);
                return;
            }

            var store = Store;
            store.RemoveExpired(Clock.UtcNow);
            List<IntakeEntry> entries;
            try
            {
                using var content = new MemoryStream();
                await Bot.Files.DownloadAsync(doc.FileId, content);
                content.Position = 0;
                entries = BatchIntake.Read(doc.FileName, content);
            }
            catch (InvalidDataException e)
            {
                // Not a bug: the upload is not a usable .xml or .zip, and the uploader is told why.
                Bot.SendReply(e.Message.FormatHTML(), command);
                return;
            }

            var id = BatchStore.NewId();
            var live = LiveDirectory();
            var master = XDocument.Load(Path.Combine(live, "English.xml"));
            var batch = BatchBuilder.Build(id, chatId, Clock.UtcNow, entries, store.FilesDirectory(id), live, master);
            store.Save(batch);
            var screen = BatchMenu.Public(batch);
            var sent = await Bot.Api.SendMessage(chatId, screen.Text, ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = command.MessageId, AllowSendingWithoutReply = true },
                replyMarkup: screen.Markup);
            store.Change(id, b => b.PublicMessageId = sent.MessageId);
        }

        /// <summary>A tap on any upload button (callback data <c>lb:…</c>).</summary>
        internal static async Task HandleAsync(CallbackQuery query, string[] args)
        {
            try
            {
                await HandleCoreAsync(query, args);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Language upload button {query.Data}");
                await AnswerAsync(query, "Something went wrong: " + e.GetBaseException().Message + "\nTap Review to try again.", true);
            }
        }

        private static async Task HandleCoreAsync(CallbackQuery query, string[] args)
        {
            if (!BatchMenu.TryParse(args, out var id, out var op, out var arg))
            {
                await AnswerAsync(query, "Unknown button.");
                return;
            }
            if (!MayReview(query.From.Id))
            {
                await AnswerAsync(query, "Only language admins can review language uploads.", true);
                return;
            }

            var store = Store;
            switch (op)
            {
                case BatchMenu.Ops.Close:
                    await CloseAsync(query);
                    await AnswerAsync(query);
                    return;
                case BatchMenu.Ops.Report:
                    await SendReportAsync(query, store.Load(id));
                    return;
                case BatchMenu.Ops.Do:
                    await DecideAsync(query, store, id, (BatchAction)arg);
                    return;
            }

            string notice = null;
            var screen = store.Change(id, b =>
            {
                if (b == null) return null;
                if (!b.IsOpen) return BatchMenu.Done(b);
                switch (op)
                {
                    case BatchMenu.Ops.Issues: return BatchMenu.Issues(b, arg);
                    case BatchMenu.Ops.ToggleIssue:
                        b.ToggleIssue(arg);
                        return BatchMenu.Issues(b, BatchMenu.PageOf(arg));
                    case BatchMenu.Ops.Files: return BatchMenu.Files(b, arg);
                    case BatchMenu.Ops.ToggleFile:
                        if (!b.ToggleFile(arg)) notice = "This file cannot be uploaded.";
                        return BatchMenu.Files(b, BatchMenu.PageOf(arg));
                    case BatchMenu.Ops.Ask:
                        if (!Enum.IsDefined(typeof(BatchAction), arg)) return BatchMenu.Summary(b);
                        if ((BatchAction)arg != BatchAction.Discard && BatchMenu.FilesFor(b, (BatchAction)arg).Count == 0)
                        {
                            notice = "Nothing to upload.";
                            return BatchMenu.Summary(b);
                        }
                        return BatchMenu.Confirm(b, (BatchAction)arg);
                    default: return BatchMenu.Summary(b); // open, sum
                }
            });
            if (screen == null)
            {
                await AnswerAsync(query, Expired, true);
                return;
            }
            await ShowAsync(query, op == BatchMenu.Ops.Open, screen);
            await AnswerAsync(query, notice);
        }

        private static async Task DecideAsync(CallbackQuery query, BatchStore store, string id, BatchAction action)
        {
            var by = query.From.FirstName;
            List<PublishOutcome> outcomes = null;
            string publicText = null;
            var live = LiveDirectory();
            var batch = store.Change(id, b =>
            {
                if (b == null || !b.IsOpen) return b;
                if (action == BatchAction.Discard)
                {
                    b.Outcome = $"Discarded by {by}.";
                    publicText = BatchMenu.Discarded(b, by);
                    return b;
                }
                var chosen = BatchMenu.FilesFor(b, action).Select(f => f.FileName).ToHashSet();
                if (chosen.Count == 0) return b;
                outcomes = new LanguagePublisher(live, fallbackLockPath: Path.Combine(StagingDirectory(), ".languages.lock"))
                    .Publish(b, chosen, store.FilesDirectory(b.Id));
                b.Outcome = $"Uploaded {outcomes.Count(o => o.Published)} of {b.Files.Count} by {by}.";
                publicText = BatchMenu.Result(b, outcomes, by);
                return b;
            });
            if (batch == null)
            {
                await AnswerAsync(query, Expired, true);
                return;
            }
            if (publicText == null)
            {
                // decided meanwhile (another admin), or nothing to upload
                await ShowAsync(query, false, batch.IsOpen ? BatchMenu.Summary(batch) : BatchMenu.Done(batch));
                await AnswerAsync(query, batch.IsOpen ? "Nothing to upload." : batch.Outcome);
                return;
            }

            store.DeleteFiles(id);
            // This edition at once; the other one notices the changed folder within seconds.
            if (outcomes?.Any(o => o.Published) == true && live == Bot.LanguageDirectory)
                Methods.IntialiseLanguages();
            await EditPublicAsync(batch, publicText);
            await ShowAsync(query, false, BatchMenu.Done(batch));
            await AnswerAsync(query, batch.Outcome);
        }

        private static async Task EditPublicAsync(LanguageBatch batch, string text)
        {
            try
            {
                if (batch.PublicMessageId == 0) throw new InvalidOperationException("no chat message");
                await Bot.Api.EditMessageText(batch.ChatId, batch.PublicMessageId, text, ParseMode.Html);
            }
            catch (Exception e)
            {
                // Expected when the chat message was deleted: the result goes out as a new message instead.
                LogHelper.Info($"Language upload {batch.Id}: editing the chat message failed: {e.GetBaseException().Message}");
                Bot.Send(text, batch.ChatId);
            }
        }

        /// <summary>
        /// Shows a menu screen to the admin who tapped: in a group as an ephemeral message (a new one when opening
        /// from the chat message, otherwise the tapped one edited), in a private chat as an ordinary message.
        /// </summary>
        private static async Task ShowAsync(CallbackQuery query, bool open, Screen screen)
        {
            var message = query.Message;
            var chatId = message.Chat.Id;
            var ephemeralId = message.EphemeralMessageId ?? 0;
            try
            {
                if (ephemeralId > 0)
                    await Bot.Api.EditEphemeralMessageText(chatId, query.From.Id, ephemeralId, screen.Text, ParseMode.Html, screen.Markup);
                else if (message.Chat.Type == ChatType.Private)
                {
                    if (open)
                        await Bot.Api.SendMessage(chatId, screen.Text, ParseMode.Html, replyMarkup: screen.Markup);
                    else
                        await Bot.Api.EditMessageText(chatId, message.MessageId, screen.Text, ParseMode.Html, replyMarkup: screen.Markup);
                }
                else
                    await Bot.Api.SendMessage(chatId, screen.Text, ParseMode.Html, replyMarkup: screen.Markup,
                        ephemeralMessageParameters: ForCallback(query));
            }
            catch (ApiRequestException e) when (e.Message.Contains("not modified"))
            {
                // the same screen again (e.g. a double tap): nothing to change
            }
        }

        private static EphemeralMessageParameters ForCallback(CallbackQuery query) =>
            new EphemeralMessageParameters { ReceiverUserId = query.From.Id, CallbackQueryId = query.Id };

        private static async Task CloseAsync(CallbackQuery query)
        {
            var message = query.Message;
            try
            {
                if (message.EphemeralMessageId is int ephemeralId && ephemeralId > 0)
                    await Bot.Api.DeleteEphemeralMessage(message.Chat.Id, query.From.Id, ephemeralId);
                else if (message.Chat.Type == ChatType.Private)
                    await Bot.Api.DeleteMessage(message.Chat.Id, message.MessageId);
            }
            catch (ApiRequestException e)
            {
                // Expected when the menu is already gone (the client dropped it): nothing left to close.
                LogHelper.Info($"Language upload: closing the menu failed: {e.Message}");
            }
        }

        private static async Task SendReportAsync(CallbackQuery query, LanguageBatch batch)
        {
            if (batch == null)
            {
                await AnswerAsync(query, Expired, true);
                return;
            }
            var chatId = query.Message.Chat.Id;
            var caption = $"Language upload {batch.Id}";
            using (var content = new MemoryStream(Encoding.UTF8.GetBytes(BatchReport.Render(batch))))
            {
                var file = InputFile.FromStream(content, $"language-upload-{batch.Id}.txt");
                if (query.Message.Chat.Type == ChatType.Private)
                    await Bot.Api.SendDocument(chatId, file, caption);
                else
                    await Bot.Api.SendDocument(chatId, file, caption, ephemeralMessageParameters: ForCallback(query));
            }
            await AnswerAsync(query);
        }

        private static async Task AnswerAsync(CallbackQuery query, string text = null, bool alert = false)
        {
            try { await Bot.Api.AnswerCallbackQuery(query.Id, text, alert); }
            catch (ApiRequestException)
            {
                // Expected: already answered (an ephemeral send with this query's id answers it), or too old.
            }
        }
    }
}
