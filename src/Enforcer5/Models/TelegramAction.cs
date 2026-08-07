using System;
using Enforcer5.Helpers;

namespace Enforcer5.Models
{
    internal enum TelegramActionType
    {
        DeleteMessage,
        BanUser,
        UnbanUser,
        RestrictUser
    }

    internal sealed class TelegramAction
    {
        public string Id { get; set; }
        public TelegramActionType Type { get; set; }
        public long ChatId { get; set; }
        public int MessageId { get; set; }
        public long UserId { get; set; }
        public long UntilDateUnix { get; set; }
        public bool CanSendMessages { get; set; }
        public bool CanSendAudios { get; set; }
        public bool CanSendDocuments { get; set; }
        public bool CanSendPhotos { get; set; }
        public bool CanSendVideos { get; set; }
        public bool CanSendVideoNotes { get; set; }
        public bool CanSendVoiceNotes { get; set; }
        public bool CanSendPolls { get; set; }
        public bool CanSendOtherMessages { get; set; }
        public bool CanAddWebPagePreviews { get; set; }
        public string DecisionOwner { get; set; }
        public string PreferredExecutor { get; set; }
        public string[] AllowedExecutors { get; set; }
        public bool Portable { get; set; }
        public string CompletionKey { get; set; }
        public int Attempt { get; set; }
        public long CreatedAtUnixMs { get; set; }
        public long DeadlineUnixMs { get; set; }

        internal static TelegramAction DeleteMessage(long chatId, int messageId, DateTimeOffset now)
        {
            return new TelegramAction
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = TelegramActionType.DeleteMessage,
                ChatId = chatId,
                MessageId = messageId,
                DecisionOwner = TelegramCapabilityStore.CurrentEdition,
                PreferredExecutor = TelegramCapabilityStore.CurrentEdition,
                AllowedExecutors = TelegramCapabilityStore.CurrentEdition == "premium"
                    ? new[] { "premium", "normal" }
                    : new[] { "normal" },
                Portable = true,
                CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
                DeadlineUnixMs = now.AddMinutes(15).ToUnixTimeMilliseconds()
            };
        }

        internal static TelegramAction Ban(long chatId, long userId, DateTimeOffset now,
            DateTimeOffset? until = null) => PortableUserAction(TelegramActionType.BanUser, chatId, userId, now, until);

        internal static TelegramAction Unban(long chatId, long userId, DateTimeOffset now) =>
            PortableUserAction(TelegramActionType.UnbanUser, chatId, userId, now);

        internal static TelegramAction Restrict(long chatId, long userId, DateTimeOffset now,
            Telegram.Bot.Types.ChatPermissions permissions, DateTimeOffset? until = null)
        {
            var action = PortableUserAction(TelegramActionType.RestrictUser, chatId, userId, now, until);
            action.CanSendMessages = permissions.CanSendMessages;
            action.CanSendAudios = permissions.CanSendAudios;
            action.CanSendDocuments = permissions.CanSendDocuments;
            action.CanSendPhotos = permissions.CanSendPhotos;
            action.CanSendVideos = permissions.CanSendVideos;
            action.CanSendVideoNotes = permissions.CanSendVideoNotes;
            action.CanSendVoiceNotes = permissions.CanSendVoiceNotes;
            action.CanSendPolls = permissions.CanSendPolls;
            action.CanSendOtherMessages = permissions.CanSendOtherMessages;
            action.CanAddWebPagePreviews = permissions.CanAddWebPagePreviews;
            return action;
        }

        private static TelegramAction PortableUserAction(TelegramActionType type, long chatId,
            long userId, DateTimeOffset now, DateTimeOffset? until = null)
        {
            return new TelegramAction
            {
                Id = Guid.NewGuid().ToString("N"), Type = type, ChatId = chatId, UserId = userId,
                UntilDateUnix = until?.ToUnixTimeSeconds() ?? 0,
                DecisionOwner = TelegramCapabilityStore.CurrentEdition,
                PreferredExecutor = TelegramCapabilityStore.CurrentEdition,
                AllowedExecutors = TelegramCapabilityStore.CurrentEdition == "premium"
                    ? new[] { "premium", "normal" } : new[] { "normal" },
                Portable = true, CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
                DeadlineUnixMs = now.AddHours(24).ToUnixTimeMilliseconds()
            };
        }
    }
}
