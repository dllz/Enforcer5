using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The edit rules (design section 4, build step 8). An attack edits old posts into something against
    /// Telegram's terms (copyrighted or illegal media), which other accounts then report to get the group
    /// deleted. Telegram sends the post's original date with every edit, so a late edit needs no record;
    /// a swapped one does:
    /// <list type="bullet">
    /// <item>every media message is recorded as its type and file unique id, in
    /// <c>media:{chat}:{yyyyMMdd of the post}</c> (field: message id), kept <see cref="Window"/>; text writes
    /// nothing, and the record starts when the bot starts recording in the chat (<c>media:since:{chat}</c>);</item>
    /// <item>an edit that changes the type or the file, or turns a post from since then without a record
    /// (text) into media, is <c>edit_swap</c>: reported and deleted, always;</item>
    /// <item>an edit more than <see cref="LateAfter"/> after the post (any post, however old) is <c>edit_late</c>:
    /// by default reported and deleted for new members, reported for established ones (each group can
    /// change it under /menu → Detection → Edits).</item>
    /// </list>
    /// Admins and approved members are left out (the engine). The admins' report says what changed and when,
    /// never the content. One HGET per edit; one pipelined HSET per media message, never waited for.
    /// </summary>
    internal static class EditCheck
    {
        internal static readonly TimeSpan Window = TimeSpan.FromDays(31);
        internal static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(10);

        internal static string RecordKey(long chatId, DateTime postedUtc) =>
            $"media:{chatId}:{postedUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

        internal static string SinceKey(long chatId) => $"media:since:{chatId}";

        /// <summary>What a post was: its type and its file's unique id; null for text and anything without a file.</summary>
        internal static string Describe(Message message)
        {
            var id = MessageFingerprint.MediaIdOf(message);
            return id == null ? null : $"{MessageFingerprint.ContentTypeOf(message)}:{id}";
        }

        /// <summary>A new group message: its media is recorded, fire and forget. Admins' posts are not (when known without a lookup).</summary>
        internal static void Record(MessageContext ctx)
        {
            try
            {
                var message = ctx.Message;
                if (message.SenderChat != null || AdminDirectory.IsKnownAdmin(ctx.ChatId, ctx.UserId)) return;
                var what = Describe(message);
                if (what == null) return;
                var posted = message.Date.ToUniversalTime();
                var key = RecordKey(ctx.ChatId, posted);
                var db = Redis.db;
                _ = db.HashSetAsync(key, message.MessageId, what, flags: CommandFlags.FireAndForget);
                var ttl = posted.Date + Window + TimeSpan.FromDays(1) - Clock.UtcNow;
                if (ttl > TimeSpan.Zero) _ = db.KeyExpireAsync(key, ttl, CommandFlags.FireAndForget);
                _ = db.StringSetAsync(SinceKey(ctx.ChatId), new DateTimeOffset(posted).ToUnixTimeSeconds(), null, When.NotExists,
                    CommandFlags.FireAndForget);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Recording the media of a message in {ctx.ChatId}");
            }
        }

        /// <summary>What an edit was, for the engine; pure, for tests.</summary>
        /// <param name="before">The record of the post, or null.</param>
        /// <param name="after">What it is now (<see cref="Describe"/>), null for text.</param>
        /// <param name="recordingSince">Since when posts in the chat are recorded; null when never.</param>
        internal static EditFinding Judge(string before, string after, DateTime postedUtc, DateTime editedUtc, DateTime? recordingSince)
        {
            var age = editedUtc - postedUtc;
            if (before != null && before != after)
                return new EditFinding(SignCatalog.EditSwap.Name, Type(before), after == null ? "text" : Type(after), age);
            if (before == null && after != null && recordingSince is { } since && postedUtc >= since && age < Window)
                return new EditFinding(SignCatalog.EditSwap.Name, "text", Type(after), age);
            if (age > LateAfter)
                return new EditFinding(SignCatalog.EditLate.Name, null, null, age);
            return null;
        }

        private static string Type(string record) => record.Split(':')[0];

        /// <summary>An edited group message. Runs as dispatched work; never throws.</summary>
        internal static async Task OnEditAsync(MessageContext ctx)
        {
            try
            {
                var message = ctx.Message;
                if (message.SenderChat != null || message.From == null || message.From.IsBot || ctx.Watched) return;
                var posted = message.Date.ToUniversalTime();
                var edited = (message.EditDate ?? ctx.ReceivedAt).ToUniversalTime();
                var after = Describe(message);
                var db = Redis.db;
                var key = RecordKey(ctx.ChatId, posted);
                var record = db.HashGetAsync(key, message.MessageId);
                var since = db.StringGetAsync(SinceKey(ctx.ChatId));
                await Task.WhenAll(record, since);
                DateTime? recordingSince = since.Result.HasValue && long.TryParse(since.Result.ToString(), out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                    : null;
                var before = record.Result.HasValue ? record.Result.ToString() : null;
                var finding = Judge(before, after, posted, edited, recordingSince);

                // The record follows the post, so a second swap is compared with the first.
                if (after != null && after != before && posted > Clock.UtcNow - Window)
                    _ = db.HashSetAsync(key, message.MessageId, after, flags: CommandFlags.FireAndForget);
                if (finding == null) return;

                var lang = ctx.Lang ?? Methods.GetGroupLanguage(ctx.ChatId).Doc;
                var hit = new SignHit(finding.Sign, System.Text.Json.JsonSerializer.Serialize(new
                {
                    from = finding.From, to = finding.To, minutes = (long)finding.Age.TotalMinutes
                }))
                { Detail = Detail(lang, finding) };
                await DetectionEngine.HitAsync(ctx.ChatId, ctx.UserId, message, hit);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Checking an edit in {ctx.ChatId}");
            }
        }

        /// <summary>"photo replaced by a sticker, 21 days after posting": what changed and when, never the content.</summary>
        internal static string Detail(XDocument lang, EditFinding finding)
        {
            var when = Age(lang, finding.Age);
            return finding.From == null
                ? Methods.GetLocaleStringNoFormat(lang, "editLateDetail", when)
                : Methods.GetLocaleStringNoFormat(lang, "editSwapDetail", TypeName(lang, finding.From), TypeName(lang, finding.To), when);
        }

        // The types with a name of their own; any other is "media".
        private static readonly string[] Named = { "text", "photo", "video", "gif", "sticker", "document", "voice", "audio", "videonote" };

        private static string TypeName(XDocument lang, string type)
        {
            var key = Array.IndexOf(Named, type) >= 0 ? $"editType_{type}" : "editType_other";
            return Methods.GetLocaleStringNoFormat(lang, key);
        }

        private static string Age(XDocument lang, TimeSpan age) =>
            age.TotalDays >= 1 ? Methods.GetLocaleStringNoFormat(lang, "editAgeDays", (int)age.TotalDays)
            : age.TotalHours >= 1 ? Methods.GetLocaleStringNoFormat(lang, "editAgeHours", (int)age.TotalHours)
            : Methods.GetLocaleStringNoFormat(lang, "editAgeMinutes", Math.Max(1, (int)age.TotalMinutes));
    }

    /// <summary>What an edit was: the sign, the types before and after a swap (null for a late edit), and how long after the post.</summary>
    internal sealed record EditFinding(string Sign, string From, string To, TimeSpan Age);
}
