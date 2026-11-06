using System;
using System.Collections.Concurrent;
using System.Linq;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// What the bot itself may do in each group: whether it was removed, whether it may post, whether it may delete.
    /// Learned from the bot's own member updates (my_chat_member) and from Telegram's refusals, which every call
    /// passes on through <see cref="TelegramGateway"/>. Before 2026-11-06 nothing kept this: in a group that had
    /// muted or removed the bot, every check, notice and log line tried again and failed again, and most of the error
    /// chat was those failures.
    /// <list type="bullet">
    /// <item>Removed (my_chat_member left or kicked): believed until the bot is added again. From a refusal ("bot was
    /// kicked", "chat not found"): for <see cref="GoneFor"/>.</item>
    /// <item>May not post (muted, or the whole group is): believed for <see cref="MutedFor"/>, then the next post is
    /// tried again, since an admin changing the group's permissions sends the bot no update. Cleared at once by a
    /// member update that lets it post, or a post that went through.</item>
    /// </list>
    /// Meanwhile the gateway answers posts to such a group itself (<see cref="RefusalFor"/>), with an error
    /// <see cref="TelegramErrors"/> sorts as Telegram's own, so every caller handles both the same way.
    /// In memory only: after a restart the first refusal teaches it again.
    /// </summary>
    internal static class BotRights
    {
        internal static TimeSpan MutedFor = TimeSpan.FromMinutes(2);
        internal static TimeSpan GoneFor = TimeSpan.FromMinutes(10);

        private sealed class State
        {
            internal DateTime? GoneUntil;    // DateTime.MaxValue: from a member update, until added again
            internal DateTime? MutedUntil;
            internal bool? CanDelete;        // from the bot's own member update
            internal long? MovedTo;          // the supergroup a basic group became
        }

        private static readonly ConcurrentDictionary<long, State> States = new();

        /// <summary>Called with the group or channel a muted state began in, for <see cref="MutedGroups"/>.</summary>
        internal static event Action<long> BecameMuted;

        /// <summary>Called when the bot may post in a group again (a post went through, or it was let post).</summary>
        internal static event Action<long> MayPostAgain;

        /// <summary>Called with a group the bot learned it was removed from, or that is gone.</summary>
        internal static event Action<long> Removed;

        /// <summary>The bot was removed from the chat, or the chat is gone.</summary>
        internal static bool IsGone(long chatId) =>
            chatId < 0 && States.TryGetValue(chatId, out var s) && s.GoneUntil is { } until && until > Clock.UtcNow;

        /// <summary>Nothing the bot posts in the chat arrives: removed, muted, or the group moved to a new id.</summary>
        internal static bool CannotPost(long chatId)
        {
            if (chatId >= 0 || !States.TryGetValue(chatId, out var s)) return false;
            var now = Clock.UtcNow;
            return s.GoneUntil is { } gone && gone > now || s.MutedUntil is { } muted && muted > now || s.MovedTo != null;
        }

        /// <summary>The bot is in the chat but may not post there (muted, or the whole group is).</summary>
        internal static bool IsMuted(long chatId) =>
            chatId < 0 && States.TryGetValue(chatId, out var s) && s.MutedUntil is { } until && until > Clock.UtcNow;

        /// <summary>The supergroup the basic group became, when Telegram said so.</summary>
        internal static long? MovedTo(long chatId) => States.TryGetValue(chatId, out var s) ? s.MovedTo : null;

        /// <summary>
        /// Whether the bot may delete others' messages in the group: false when it is not an admin or lacks the right,
        /// true when it has it, null when it does not know. From the bot's own member update, else the admin list at
        /// hand (asked for in the background when there is none).
        /// </summary>
        internal static bool? MayDelete(long chatId)
        {
            if (chatId >= 0) return null;
            if (States.TryGetValue(chatId, out var s) && s.CanDelete is { } known) return known;
            var admins = AdminDirectory.Known(chatId);
            if (admins == null)
            {
                _ = AdminDirectory.GetAsync(chatId);
                return null;
            }
            var me = admins.FirstOrDefault(a => a.User.Id == Bot.Me?.Id);
            return me switch
            {
                null => false,
                ChatMemberOwner => true,
                ChatMemberAdministrator admin => admin.CanDeleteMessages,
                _ => false
            };
        }

        /// <summary>The bot's own member update in <paramref name="update"/>'s chat.</summary>
        internal static void OnMyChatMember(ChatMemberUpdated update)
        {
            var chatId = update?.Chat?.Id ?? 0;
            if (chatId >= 0) return;
            var s = States.GetOrAdd(chatId, _ => new State());
            var wasMuted = IsMuted(chatId);
            var wasGone = IsGone(chatId);
            switch (update.NewChatMember)
            {
                case ChatMemberLeft:
                case ChatMemberBanned:
                    s.GoneUntil = DateTime.MaxValue;
                    s.CanDelete = false;
                    break;
                case ChatMemberRestricted restricted:
                    s.GoneUntil = restricted.IsMember ? null : DateTime.MaxValue;
                    s.CanDelete = false;
                    s.MutedUntil = restricted.CanSendMessages ? null : Clock.UtcNow + MutedFor;
                    break;
                case ChatMemberAdministrator admin:
                    s.GoneUntil = null;
                    s.MutedUntil = null;
                    s.CanDelete = admin.CanDeleteMessages;
                    break;
                default:
                    // A plain member: posts unless the whole group is muted, which only a post finds out.
                    s.GoneUntil = null;
                    s.MutedUntil = null;
                    s.CanDelete = false;
                    break;
            }
            var mutedNow = IsMuted(chatId);
            var goneNow = IsGone(chatId);
            if (goneNow && !wasGone) Removed?.Invoke(chatId);
            else if (mutedNow && !wasMuted) BecameMuted?.Invoke(chatId);
            if (!mutedNow && !goneNow && (wasMuted || wasGone)) MayPostAgain?.Invoke(chatId);
        }

        /// <summary>A call to <paramref name="chatId"/> failed: what it says about the bot there.</summary>
        internal static void Failed(long chatId, string method, Exception e)
        {
            if (chatId >= 0 || e is KnownRefusalException) return;
            var kind = TelegramErrors.Classify(e);
            var now = Clock.UtcNow;
            switch (kind)
            {
                case TelegramErrorKind.ChatGone:
                {
                    var s = States.GetOrAdd(chatId, _ => new State());
                    if (s.GoneUntil is { } until && until > now) return;
                    s.GoneUntil = now + GoneFor;
                    LogHelper.Info($"BotRights: {chatId} is gone ({method}: {TelegramErrors.Find(e)?.Message})");
                    Removed?.Invoke(chatId);
                    break;
                }
                case TelegramErrorKind.NoSendRights when IsPost(method):
                {
                    var s = States.GetOrAdd(chatId, _ => new State());
                    var was = s.MutedUntil is { } m && m > now;
                    s.MutedUntil = now + MutedFor;
                    if (!was)
                    {
                        LogHelper.Info($"BotRights: may not post in {chatId} ({method}: {TelegramErrors.Find(e)?.Message})");
                        BecameMuted?.Invoke(chatId);
                    }
                    break;
                }
                case TelegramErrorKind.Migrated when TelegramErrors.MigratedTo(e) is { } to:
                    States.GetOrAdd(chatId, _ => new State()).MovedTo = to;
                    GroupMigration.Learned(chatId, to);
                    break;
            }
        }

        /// <summary>A call to <paramref name="chatId"/> went through.</summary>
        internal static void Succeeded(long chatId, string method)
        {
            if (chatId >= 0 || !States.TryGetValue(chatId, out var s)) return;
            var cleared = false;
            // getChat answers for public chats the bot is not in, and getChatMember is how it looks again.
            if (s.GoneUntil != null && method is not ("getChat" or "getChatMember" or "leaveChat")) { s.GoneUntil = null; cleared = true; }
            if (s.MutedUntil != null && IsPost(method)) { s.MutedUntil = null; cleared = true; }
            if (cleared) MayPostAgain?.Invoke(chatId);
        }

        /// <summary>
        /// The error the gateway answers <paramref name="request"/> with itself, without asking Telegram, when the bot
        /// knows it would be refused; null to send it. Only posts and, in a chat the bot was removed from, everything
        /// but the calls that look again (getChat, getChatMember, leaveChat).
        /// </summary>
        internal static Exception RefusalFor<T>(IRequest<T> request, long chatId)
        {
            if (chatId >= 0 || !States.TryGetValue(chatId, out var s)) return null;
            var now = Clock.UtcNow;
            var method = request.MethodName;
            if (s.GoneUntil is { } gone && gone > now && method is not ("getChat" or "getChatMember" or "leaveChat"))
                return new KnownRefusalException("Forbidden: bot is not a member of the chat (known to the bot)", 403);
            if (IsPost(method) && s.MutedUntil is { } muted && muted > now)
                return new KnownRefusalException("Bad Request: not enough rights to send text messages to the chat (known to the bot)", 400);
            if (IsPost(method) && s.MovedTo is { } to)
                return new KnownRefusalException("Bad Request: group chat was upgraded to a supergroup chat (known to the bot)", 400,
                    new ResponseParameters { MigrateToChatId = to });
            return null;
        }

        // A new message in the chat: what muting stops. Edits, deletes and button answers are not.
        private static bool IsPost(string method) =>
            method.StartsWith("send", StringComparison.Ordinal) && method != "sendChatAction" ||
            method.StartsWith("forward", StringComparison.Ordinal) || method.StartsWith("copy", StringComparison.Ordinal);

        internal static void ResetForTests() => States.Clear();
    }

    /// <summary>A refusal the gateway answered itself from <see cref="BotRights"/>, without asking Telegram.</summary>
    internal sealed class KnownRefusalException : ApiRequestException
    {
        internal KnownRefusalException(string message, int errorCode, ResponseParameters parameters = null)
            : base(message, errorCode, parameters)
        {
        }
    }
}
