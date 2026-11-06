using System;
using Telegram.Bot.Exceptions;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// What a failed Telegram call means for the bot. Everything but <see cref="Other"/> is an expected outcome: the
    /// caller handles it, and <see cref="ErrorReporter"/> keeps it out of the error chat.
    /// </summary>
    internal enum TelegramErrorKind
    {
        /// <summary>Not one of the cases below: a real error, worth reporting.</summary>
        Other,
        /// <summary>A 429, Telegram's or the gateway's own.</summary>
        RateLimited,
        /// <summary>The bot may not post in the chat at all: it is muted, or the whole group is.</summary>
        NoSendRights,
        /// <summary>The bot may post text there, but not this kind of media.</summary>
        NoMediaRights,
        /// <summary>The bot lacks an admin right for the call (restrict, delete, pin, post in a channel).</summary>
        NoAdminRights,
        /// <summary>The chat cannot be reached: the bot was removed, the chat deleted, the user blocked or never started the bot.</summary>
        ChatGone,
        /// <summary>The group became a supergroup and has a new id (<see cref="TelegramErrors.MigratedTo"/>).</summary>
        Migrated,
        /// <summary>The message to edit, delete or reply to is gone or too old, or the edit changed nothing.</summary>
        MessageGone,
        /// <summary>The forum topic is closed or gone.</summary>
        TopicClosed,
        /// <summary>A button tap or inline query Telegram no longer takes an answer for.</summary>
        QueryTooOld,
        /// <summary>The call needs a supergroup and this is a basic group.</summary>
        NotSupergroup,
        /// <summary>The person acted on is an admin or the owner, or the bot itself.</summary>
        TargetIsAdmin,
        /// <summary>The person is not (or no longer) in the chat, or Telegram does not know them.</summary>
        MemberGone,
    }

    /// <summary>
    /// Sorts Telegram's errors by what they mean, from the error description: Telegram documents nothing beyond the
    /// HTTP status, and the descriptions are what the wording rules below are matched on (without case, loosely, since
    /// Telegram has reworded them before). The one place this is decided; ported from blackwolf (2026-11-04), with the
    /// kinds Enforcer's moderation needs on top.
    /// </summary>
    internal static class TelegramErrors
    {
        /// <summary>The kind of a failed call; unwraps AggregateException and other wrappers. Not a Telegram error: Other.</summary>
        internal static TelegramErrorKind Classify(Exception e)
        {
            var api = Find(e);
            if (api == null) return TelegramErrorKind.Other;
            if (api.ErrorCode == 429) return TelegramErrorKind.RateLimited;
            if (api.Parameters?.MigrateToChatId is { } to && to != 0) return TelegramErrorKind.Migrated;
            var kind = Classify(api.Message);
            if (kind == TelegramErrorKind.Other && api.ErrorCode == 403) return TelegramErrorKind.ChatGone;
            return kind;
        }

        /// <summary>The kind for Telegram's error description, e.g. "Bad Request: not enough rights to send text messages to the chat".</summary>
        internal static TelegramErrorKind Classify(string description)
        {
            if (string.IsNullOrEmpty(description)) return TelegramErrorKind.Other;
            var d = description.ToLowerInvariant();
            bool Has(string s) => d.Contains(s);

            if (Has("too many requests")) return TelegramErrorKind.RateLimited;
            if (Has("upgraded to a supergroup")) return TelegramErrorKind.Migrated;
            if (Has("topic_closed") || Has("topic_deleted") || Has("message thread not found"))
                return TelegramErrorKind.TopicClosed;
            if (Has("not enough rights to send text") || Has("not enough rights to send messages") ||
                Has("chat_write_forbidden") || Has("have no rights to send a message") || Has("chat_restricted"))
                return TelegramErrorKind.NoSendRights;
            if (Has("not enough rights to send") || Has("chat_send_"))
                return TelegramErrorKind.NoMediaRights;
            if (Has("only for supergroups") || Has("available only for supergroup"))
                return TelegramErrorKind.NotSupergroup;
            if (Has("not enough rights") || Has("chat_admin_required") || Has("need administrator rights") ||
                Has("not an administrator"))
                return TelegramErrorKind.NoAdminRights;
            if (Has("bot was kicked") || Has("bot is not a member") || Has("chat not found") || Has("chat was deleted") ||
                Has("chat was deactivated") || Has("bot was blocked by the user") || Has("user is deactivated") ||
                Has("bot can't initiate conversation") || Has("bot can't send messages to bots") || Has("peer_id_invalid") ||
                Has("channel_private") || Has("group chat was deactivated"))
                return TelegramErrorKind.ChatGone;
            if (Has("query is too old") || Has("query id is invalid") || Has("query_id_invalid"))
                return TelegramErrorKind.QueryTooOld;
            if (Has("administrator of the chat") || Has("user is an administrator") || Has("chat owner") ||
                Has("can't restrict self") || Has("can't demote chat creator") || Has("user_admin_invalid"))
                return TelegramErrorKind.TargetIsAdmin;
            if (Has("user not found") || Has("member not found") || Has("participant_id_invalid") ||
                Has("user_id_invalid") || Has("user_not_participant"))
                return TelegramErrorKind.MemberGone;
            if (Has("message") && (Has("not found") || Has("can't be edited") || Has("can't be deleted") ||
                Has("is not modified") || Has("message_id_invalid") || Has("to be replied not found")))
                return TelegramErrorKind.MessageGone;
            return TelegramErrorKind.Other;
        }

        /// <summary>True for every expected kind: nothing to report.</summary>
        internal static bool IsExpected(Exception e) => Classify(e) != TelegramErrorKind.Other;

        /// <summary>True when nothing the bot posts in the chat arrives: muted, removed, or moved to a new id.</summary>
        internal static bool CannotPost(TelegramErrorKind kind) =>
            kind is TelegramErrorKind.NoSendRights or TelegramErrorKind.ChatGone or TelegramErrorKind.Migrated;

        /// <summary>The supergroup a basic group became, when Telegram said so; null otherwise.</summary>
        internal static long? MigratedTo(Exception e) =>
            Find(e)?.Parameters?.MigrateToChatId is { } to && to != 0 ? to : null;

        /// <summary>The Telegram error in <paramref name="e"/>, through any wrappers; null when there is none.</summary>
        internal static ApiRequestException Find(Exception e)
        {
            for (var depth = 0; e != null && depth < 10; depth++)
            {
                if (e is ApiRequestException api) return api;
                if (e is AggregateException agg && agg.InnerExceptions.Count > 0)
                    e = agg.InnerExceptions[0];
                else
                    e = e.InnerException;
            }
            return null;
        }
    }
}
