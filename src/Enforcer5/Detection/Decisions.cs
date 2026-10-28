using System;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Every decision about a member, recorded as a label in <c>member_labels</c> so Detection can
    /// learn a group's rules from its admins (Daniel, 2026-10-27): bans, kicks, mutes, warns and
    /// deletes, and their undoing, whether an admin took them with a command, a button, from a report
    /// or in the Telegram app itself, or the bot took them on its own (<c>auto</c>).
    /// <para>
    /// Written through <see cref="HistoryWriter"/>, after the sanction or warn it belongs to, so it
    /// never holds up the action and is dropped like the rest of the history while PostgreSQL is not
    /// ready. Never message text: what the decision was about is the message's type, a hash of its
    /// text and its file's unique id.
    /// </para>
    /// </summary>
    internal static class Decisions
    {
        /// <summary>Telegram's account for anonymous admins; an action it takes is an admin's.</summary>
        internal const long AnonymousAdmin = 1087968824;

        /// <summary>A sanction or warn Telegram accepted (<paramref name="action"/>: ban, tempban, kick, mute, tempmute, warn).</summary>
        internal static void Sanction(long chatId, long userId, string action, long? actorId, string reason,
            bool fromReport, string key = null) =>
            Record(chatId, userId, "bad", action, actorId, reason, fromReport, key);

        /// <summary>A decision taken back (<paramref name="action"/>: unban, unmute, unwarn).</summary>
        internal static void Undo(long chatId, long userId, string action, long? actorId, string key = null) =>
            Record(chatId, userId, "undo", action, actorId, null, false, key);

        /// <summary>An admin deleted <paramref name="message"/> with /delmsg.</summary>
        internal static void Deleted(Message message, long actorId)
        {
            var target = message?.From?.Id;
            if (target == null || message.SenderChat != null) return; // a channel's post: nobody to label
            Record(message.Chat.Id, target.Value, "bad", "delete", actorId, null, false, null, message);
        }

        /// <summary>
        /// An admin looked at a member and found nothing wrong (Fine on a report, Ignore on a raid alert or a
        /// link report). <paramref name="signs"/> are what they were reported for, which Fine exempts them
        /// from for a while; kept as the label's reason.
        /// </summary>
        internal static void Fine(long chatId, long userId, long actorId, System.Collections.Generic.IEnumerable<string> signs) =>
            Record(chatId, userId, "fine", "fine", actorId, string.Join(",", signs ?? System.Array.Empty<string>()), false, null);

        /// <summary>
        /// The buttons whose decisions are labelled with their own source: Detection reports, and the raid
        /// alert's and the link report's buttons. Every other button is <c>button</c>.
        /// </summary>
        internal static string ButtonSource(string callbackData)
        {
            var trigger = callbackData?.Split(':')[0];
            return trigger switch
            {
                DetectionReports.Trigger => "report",
                "raid" or "raidlink" => "raid",
                _ => null
            };
        }

        /// <summary>An admin deleted the member's message <paramref name="messageId"/> from a report's button.</summary>
        internal static void Deleted(long chatId, long userId, long messageId, long actorId) =>
            Record(chatId, userId, "bad", "delete", actorId, null, true, null, aboutId: messageId);

        private static void Record(long chatId, long userId, string label, string action, long? actorId, string reason,
            bool fromReport, string key, Message about = null, long? aboutId = null)
        {
            try
            {
                if (chatId > 0 || userId == Bot.Me?.Id) return;
                var update = ErrorReporter.Current?.Update;
                var buttonSource = ButtonSource(update?.CallbackQuery?.Data);
                // What raid mode does when an admin presses a raid button (ban the suspects) is theirs.
                if (actorId == null && key == null && buttonSource == "raid") actorId = update.CallbackQuery.From.Id;
                var source = key != null ? "app"
                    : fromReport ? "report"
                    : actorId == null ? "auto"
                    : buttonSource != null ? buttonSource
                    : update?.CallbackQuery != null ? "button"
                    : "command";
                about ??= AboutMessage(update, userId, actorId);
                var fingerprint = about != null ? MessageFingerprint.Of(about) : null;
                var labelEvent = new LabelEvent(chatId, userId, label, action, source, actorId,
                    about?.MessageId ?? aboutId, fingerprint?.ContentType, fingerprint?.TextHash, fingerprint?.MediaUniqueId,
                    // Only the bot's own reasons, which are fixed words (Flood, Media, ...), and the signs a
                    // Fine was about; an admin's reason may be anything.
                    source == "auto" || action == "fine" ? HistoryWriter.Trim(reason) : null,
                    key, BanHistory.Edition, DateTimeOffset.UtcNow);
                HistoryWriter.Enqueue(async () =>
                    {
                        // Once per decision: the other edition may have recorded an app change first.
                        if (await Repositories.DetectionLog.RecordLabelAsync(labelEvent) is { } labelId)
                            TrustLearning.Decided(labelEvent, labelId);
                    },
                    $"Recording the {action} of {userId} in {chatId} for Detection");
            }
            catch (Exception e)
            {
                // Never in the way of the action itself.
                ErrorReporter.Report(e, $"Recording the {action} of {userId} in {chatId} for Detection");
            }
        }

        /// <summary>
        /// The member's message the decision was about: the one an admin's command replied to, or, for
        /// the bot's own decision, the message being handled. Null when it was not theirs.
        /// </summary>
        private static Message AboutMessage(Update update, long userId, long? actorId)
        {
            var message = update?.Message;
            if (message == null) return null;
            var candidate = actorId != null ? message.ReplyToMessage : message;
            return candidate?.From?.Id == userId && candidate.SenderChat == null ? candidate : null;
        }

        /// <summary>
        /// A member update (chat_member): bans, kicks, mutes and their undoing that an admin made in the
        /// Telegram app itself. Recorded in the ban history and as labels. The bots' own changes are
        /// skipped (each records its own), as are a member's own joins and leaves.
        /// </summary>
        internal static void FromChatMember(ChatMemberUpdated update)
        {
            try
            {
                var user = update?.NewChatMember?.User;
                var by = update?.From;
                if (user == null || by == null || by.Id == user.Id || user.Id == Bot.Me?.Id) return;
                if (by.IsBot && by.Id != AnonymousAdmin) return;
                long? actor = by.Id == AnonymousAdmin ? null : by.Id;
                var chatId = update.Chat.Id;
                var at = RaidCapture.Utc(update.Date);
                string Key(string what) => $"app:{chatId}:{user.Id}:{at.ToUnixTimeSeconds()}:{what}";
                const string Reason = "In the Telegram app";

                // The ban history labels each of these too (source app, from the key).
                switch (Change(update.OldChatMember, update.NewChatMember, at.UtcDateTime))
                {
                    case "ban":
                        BanHistory.Banned(chatId, user.Id, SanctionKind.Ban, Reason, actor, key: Key("ban"));
                        break;
                    case "tempban":
                        BanHistory.Banned(chatId, user.Id, SanctionKind.Tempban, Reason, actor,
                            ((ChatMemberBanned)update.NewChatMember).UntilDate, key: Key("tempban"));
                        break;
                    case "kick":
                        BanHistory.Kicked(chatId, user.Id, Reason, actor, key: Key("kick"));
                        break;
                    case "unban":
                        BanHistory.Unbanned(chatId, user.Id, BanEnd.Unbanned, actor, Key("unban"));
                        break;
                    case "mute":
                        var until = ((ChatMemberRestricted)update.NewChatMember).UntilDate;
                        BanHistory.Muted(chatId, user.Id, Reason, actor, until, Key(until.HasValue ? "tempmute" : "mute"));
                        break;
                    case "unmute":
                        BanHistory.Unmuted(chatId, user.Id, actor, Key("unmute"));
                        break;
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Recording an admin's change of a member made in the Telegram app");
            }
        }

        /// <summary>What an admin did to a member, from their status before and after; null for anything else.</summary>
        internal static string Change(ChatMember before, ChatMember after, DateTime nowUtc)
        {
            static bool Muted(ChatMember m) => m is ChatMemberRestricted { CanSendMessages: false };
            static bool In(ChatMember m) => m is ChatMemberMember or ChatMemberAdministrator or ChatMemberOwner ||
                                            m is ChatMemberRestricted { IsMember: true };
            switch (after)
            {
                case ChatMemberBanned banned when before is not ChatMemberBanned:
                    // Telegram reports "forever" as no date, or a date more than 366 days away.
                    return banned.UntilDate is { } until && until > nowUtc && until < nowUtc.AddDays(366) ? "tempban" : "ban";
                case ChatMemberLeft when before is ChatMemberBanned:
                    return "unban";
                case ChatMemberLeft when In(before):
                    return "kick";
            }
            if (Muted(after) && !Muted(before) && In(after)) return "mute";
            if (Muted(before) && !Muted(after) && after is not ChatMemberBanned && after is not ChatMemberLeft) return "unmute";
            return null;
        }
    }
}
