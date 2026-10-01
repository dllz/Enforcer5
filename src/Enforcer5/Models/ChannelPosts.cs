using System;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Models
{
    /// <summary>
    /// What a chat has configured about one channel that posted in it, read in one batch for the
    /// message being handled.
    /// </summary>
    /// <param name="Enabled">The chat removes posts made as a channel.</param>
    /// <param name="SenderAllowed">This channel is on the chat's allowlist.</param>
    /// <param name="LinkedChannelId">
    /// The chat's linked channel from the short-lived cache: null when not cached, 0 when the chat
    /// has none.
    /// </param>
    internal sealed record ChannelPostPolicy(bool Enabled, bool SenderAllowed, long? LinkedChannelId);

    /// <summary>A channel on a chat's allowlist, with the title it had when it was added.</summary>
    internal sealed record AllowedChannel(long Id, string Title)
    {
        public override string ToString() => string.IsNullOrEmpty(Title) ? Id.ToString() : $"{Title} ({Id})";
    }

    internal enum ChannelPostDecision
    {
        /// <summary>Not a post made as a channel, or the chat has the setting off. Handle as usual.</summary>
        Ignore,

        /// <summary>A channel post the chat accepts. It skips the per-user filters.</summary>
        Exempt,

        /// <summary>Delete the message and ban the channel.</summary>
        Remove,

        /// <summary>As <see cref="Remove"/>, unless a lookup shows the sender is the linked channel.</summary>
        RemoveUnlessLinked
    }

    internal enum ChannelTargetKind
    {
        /// <summary>No channel was named; the command is about a user.</summary>
        None,

        /// <summary>A channel.</summary>
        Channel,

        /// <summary>The group itself, which is how anonymous admins post. Never a target.</summary>
        Group
    }

    /// <summary>
    /// Posts made "as a channel". Telegram shows bots only the channel, never the member behind it:
    /// <c>Message.From</c> is a placeholder account shared by every such post. So the channel is
    /// what can be acted on, by deleting its message and banning it from the chat.
    /// </summary>
    internal static class ChannelPosts
    {
        /// <summary>Telegram's placeholder sender for posts made as a channel (@Channel_Bot).</summary>
        internal const long PlaceholderSenderId = 136817688;

        /// <summary>
        /// Decides what to do with <paramref name="message"/> in <paramref name="chatId"/>. Pure: the
        /// policy is read beforehand, so this needs neither Redis nor Telegram.
        /// </summary>
        internal static ChannelPostDecision Decide(Message message, long chatId, ChannelPostPolicy policy)
        {
            var sender = message?.SenderChat;
            // Anonymous admins post as the group itself.
            if (sender == null || sender.Id == chatId) return ChannelPostDecision.Ignore;
            if (policy == null || !policy.Enabled) return ChannelPostDecision.Ignore;

            // A linked channel's posts arrive as automatic forwards; removing them would break the
            // channel's comment threads.
            if (message.IsAutomaticForward || policy.SenderAllowed) return ChannelPostDecision.Exempt;

            if (policy.LinkedChannelId == null) return ChannelPostDecision.RemoveUnlessLinked;
            return policy.LinkedChannelId.Value == sender.Id ? ChannelPostDecision.Exempt : ChannelPostDecision.Remove;
        }

        /// <summary>
        /// The channel a /ban or /unban is about. Replying to a post made as a channel names that
        /// channel; without a reply, a negative chat id as the first word does. User ids are always
        /// positive, so the two cannot be confused. With a reply the argument is the reason, so it is
        /// not read.
        /// </summary>
        internal static ChannelTargetKind FromModerationCommand(Message command, string argument, long chatId,
            out long channelId, out string title)
        {
            channelId = 0;
            title = null;
            var reply = command?.ReplyToMessage;
            if (reply != null)
            {
                var sender = reply.SenderChat;
                if (sender == null) return ChannelTargetKind.None;
                channelId = sender.Id;
                title = sender.Title;
                return sender.Id == chatId ? ChannelTargetKind.Group : ChannelTargetKind.Channel;
            }

            if (!TryParseChannelId(FirstWord(argument), out channelId)) return ChannelTargetKind.None;
            return channelId == chatId ? ChannelTargetKind.Group : ChannelTargetKind.Channel;
        }

        /// <summary>
        /// The channel a replied-to message identifies: the channel it was posted as, or the channel
        /// a forwarded post came from. Null when the message identifies no channel.
        /// </summary>
        internal static Chat FromReply(Message reply)
        {
            if (reply == null) return null;
            if (reply.SenderChat != null) return reply.SenderChat;
            return reply.ForwardOrigin is MessageOriginChannel origin ? origin.Chat : null;
        }

        /// <summary>A channel or supergroup id: Telegram gives every chat other than a user a negative id.</summary>
        internal static bool TryParseChannelId(string text, out long channelId) =>
            long.TryParse(text, out channelId) && channelId < 0;

        /// <summary>A public channel's @username, without validating more than its shape.</summary>
        internal static bool IsUsername(string text) =>
            text != null && text.Length > 1 && text[0] == '@' && text.IndexOf(' ') < 0;

        internal static string FirstWord(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.Trim();
            var space = trimmed.IndexOf(' ');
            return space < 0 ? trimmed : trimmed.Substring(0, space);
        }

        /// <summary>How a channel is shown to admins and in the log channel.</summary>
        internal static string Describe(Chat channel) =>
            channel == null ? "" :
            channel.Username != null ? $"{channel.Title} (@{channel.Username}, {channel.Id})" : $"{channel.Title} ({channel.Id})";

        /// <summary>
        /// Whether a value in the tempban hash belongs to this user in this chat. Values are
        /// "{chatId}:{userId}:{name}:{group}" (see Commands.Tempban), so a prefix match is needed.
        /// </summary>
        internal static bool IsTempbanEntryFor(string value, long chatId, long userId)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var prefix = $"{chatId}:{userId}";
            return value == prefix || value.StartsWith(prefix + ":", StringComparison.Ordinal);
        }
    }
}
