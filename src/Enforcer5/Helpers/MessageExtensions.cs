using System.Linq;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Bridges shapes the 13.x Telegram library exposed that 22.x dropped.
    /// </summary>
    public static class MessageExtensions
    {
        extension(Message message)
        {
            /// <summary>
            /// The first newly joined member, or null. 13.x exposed a single NewChatMember;
            /// 22.x only has the NewChatMembers array. Telegram sends one member per update in
            /// practice, so the first entry is the one every caller here wants.
            /// </summary>
            public User NewChatMember => message?.NewChatMembers?.FirstOrDefault();

            /// <summary>
            /// True for Telegram's service notifications (joins, leaves, title/photo changes).
            /// 13.x collapsed these into a single MessageType.ServiceMessage.
            /// </summary>
            public bool IsServiceMessage =>
                message != null &&
                (message.Type == MessageType.NewChatMembers ||
                 message.Type == MessageType.LeftChatMember ||
                 message.Type == MessageType.NewChatTitle ||
                 message.Type == MessageType.NewChatPhoto ||
                 message.Type == MessageType.DeleteChatPhoto ||
                 message.Type == MessageType.GroupChatCreated ||
                 message.Type == MessageType.SupergroupChatCreated ||
                 message.Type == MessageType.ChannelChatCreated ||
                 message.Type == MessageType.MigrateToChatId ||
                 message.Type == MessageType.MigrateFromChatId ||
                 message.Type == MessageType.PinnedMessage);
        }
    }
}
