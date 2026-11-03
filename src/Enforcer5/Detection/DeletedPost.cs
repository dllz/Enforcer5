using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// A post Detection deleted, as its report shows it (Daniel, 2026-11-03): a link to a deleted message goes
    /// nowhere, so the report quotes it instead. Text and captions are quoted (cut at <see cref="MaxText"/>);
    /// a file is only described, never posted with the report: the edit attack swaps posts into media against
    /// Telegram's terms to get the group reported, and the bot must not be the one spreading it. An admin can
    /// ask for the file with Show deleted post: it goes to them alone, in private, protected from forwarding
    /// and saving.
    /// </summary>
    /// <param name="Kind">Its kind (<see cref="MessageFingerprint.ContentTypeOf"/>).</param>
    /// <param name="FileId">The file to send on Show; null for text and anything without one.</param>
    /// <param name="Facts">What Telegram says about the file (length, name, size, title), language neutral; null when nothing.</param>
    /// <param name="Text">The text or caption, cut at <see cref="DeletedPost.MaxText"/>; null when none.</param>
    internal sealed record DeletedPost(long MessageId, string Kind, string FileId, string Facts, string Text)
    {
        internal const int MaxText = 500;

        /// <summary>How long one is kept: the life of a report.</summary>
        internal static readonly TimeSpan Keep = DetectionReports.Window;

        /// <summary>What the report needs of <paramref name="message"/>; null when there is none.</summary>
        internal static DeletedPost From(Message message)
        {
            if (message == null) return null;
            var text = message.Text ?? message.Caption;
            if (text != null && text.Length > MaxText) text = text[..(MaxText - 1)] + "…";
            if (string.IsNullOrWhiteSpace(text)) text = null;
            return new DeletedPost(message.MessageId, MessageFingerprint.ContentTypeOf(message), FileIdOf(message), FactsOf(message), text);
        }

        private static string FileIdOf(Message message) =>
            message.Photo?.OrderByDescending(p => p.Width * p.Height).FirstOrDefault()?.FileId
            ?? message.Animation?.FileId
            ?? message.Video?.FileId
            ?? message.Sticker?.FileId
            ?? message.Document?.FileId
            ?? message.Voice?.FileId
            ?? message.Audio?.FileId
            ?? message.VideoNote?.FileId;

        /// <summary>"0:42 · Title – Artist · song.mp3 · 3.1 MB": what is known about the file. Pure, for tests.</summary>
        internal static string FactsOf(Message message)
        {
            var facts = new List<string>();
            void Add(string fact)
            {
                if (!string.IsNullOrWhiteSpace(fact)) facts.Add(fact.Trim());
            }
            if (message.Audio is { } audio)
            {
                Add(Length(audio.Duration));
                Add(string.Join(" – ", new[] { audio.Title, audio.Performer }.Where(s => !string.IsNullOrWhiteSpace(s))));
                Add(audio.FileName);
                Add(Size(audio.FileSize));
            }
            else if (message.Video is { } video)
            {
                Add(Length(video.Duration));
                Add(video.FileName);
                Add(Size(video.FileSize));
            }
            else if (message.Animation is { } gif)
            {
                Add(Length(gif.Duration));
                Add(Size(gif.FileSize));
            }
            else if (message.Voice is { } voice)
            {
                Add(Length(voice.Duration));
            }
            else if (message.VideoNote is { } note)
            {
                Add(Length(note.Duration));
            }
            else if (message.Document is { } document)
            {
                Add(document.FileName);
                Add(Size(document.FileSize));
            }
            else if (message.Sticker is { } sticker)
            {
                Add(sticker.Emoji);
                Add(sticker.SetName);
            }
            return facts.Count == 0 ? null : string.Join(" · ", facts);
        }

        private static string Length(int seconds) =>
            seconds <= 0 ? null
            : seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);

        private static string Size(long? bytes) =>
            bytes is not > 0 ? null
            : bytes >= 1024 * 1024 ? (bytes.Value / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : Math.Max(1, bytes.Value / 1024) + " KB";

        /// <summary>
        /// The report's block about it: a heading, then a quote (collapsed) with its kind and facts, then its text.
        /// HTML, every part escaped.
        /// </summary>
        internal string Html(XDocument lang)
        {
            var lines = new List<string>();
            if (Kind != "text")
            {
                var kind = Methods.GetLocaleStringNoFormat(lang, EditCheck.TypeKey(Kind));
                lines.Add(Facts != null ? $"{kind.FormatHTML()} · {Facts.FormatHTML()}" : kind.FormatHTML());
            }
            if (Text != null) lines.Add(Text.FormatHTML());
            return Methods.GetLocaleString(lang, "reportDeletedPost") + "\n<blockquote expandable>" + string.Join("\n", lines) + "</blockquote>";
        }

        /// <summary>
        /// Sends the file to <paramref name="to"/> (an admin's private chat), protected from forwarding and saving,
        /// without a caption. Throws what Telegram answers (403: they never started the bot; 400: the file is gone).
        /// </summary>
        internal Task SendAsync(long to)
        {
            var file = InputFile.FromFileId(FileId);
            return Kind switch
            {
                "photo" => Bot.Api.SendPhoto(to, file, protectContent: true),
                "gif" => Bot.Api.SendAnimation(to, file, protectContent: true),
                "video" => Bot.Api.SendVideo(to, file, protectContent: true),
                "sticker" => Bot.Api.SendSticker(to, file, protectContent: true),
                "voice" => Bot.Api.SendVoice(to, file, protectContent: true),
                "audio" => Bot.Api.SendAudio(to, file, protectContent: true),
                "videonote" => Bot.Api.SendVideoNote(to, file, protectContent: true),
                _ => Bot.Api.SendDocument(to, file, protectContent: true)
            };
        }
    }
}
