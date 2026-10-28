using System;
using Enforcer5.Attributes;
using Enforcer5.Detection;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5
{
    /// <summary>
    /// /banmedia and /unbanmedia (Detection step 12): an admin bans the file of the message they reply to in the group, so
    /// posting it again deletes it, or lifts that ban. <c>/banmedia</c> alone says how many files are banned;
    /// <c>/unbanmedia all</c> lifts every one.
    /// </summary>
    public static partial class Commands
    {
        [Command(Trigger = "banmedia", InGroupOnly = true, GroupAdminOnly = true)]
        public static void BanMedia(Update update, string[] args)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            var target = message.ReplyToMessage;
            if (target == null)
            {
                var count = BannedMedia.CountAsync(chatId).GetAwaiter().GetResult();
                Bot.SendReply(Methods.GetLocaleString(lang, "banMediaCount", count) + "\n\n" + Methods.GetLocaleString(lang, "hcommandbanmedia"), update);
                return;
            }
            var file = MessageFingerprint.MediaIdOf(target);
            if (file == null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "banMediaNoFile"), update);
                return;
            }
            var added = BannedMedia.Add(chatId, file, MessageFingerprint.ContentTypeOf(target), message.From.Id, "command");
            // The post itself goes too, unless an admin made it.
            if (target.From != null && !Methods.IsGroupAdmin(target.From.Id, chatId))
                EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, target.MessageId)).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, added ? "banMediaAdded" : "banMediaAlready"), update);
            Service.LogCommand(update, message.Text);
        }

        [Command(Trigger = "unbanmedia", InGroupOnly = true, GroupAdminOnly = true)]
        public static void UnbanMedia(Update update, string[] args)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            var word = (args.Length > 1 ? args[1] : null)?.Trim();
            if (string.Equals(word, "all", StringComparison.OrdinalIgnoreCase))
            {
                BannedMedia.CountAsync(chatId).GetAwaiter().GetResult();
                Bot.SendReply(Methods.GetLocaleString(lang, "banMediaCleared", BannedMedia.Clear(chatId)), update);
                Service.LogCommand(update, message.Text);
                return;
            }
            var file = message.ReplyToMessage != null ? MessageFingerprint.MediaIdOf(message.ReplyToMessage) : null;
            if (file == null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "hcommandunbanmedia"), update);
                return;
            }
            BannedMedia.CountAsync(chatId).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, BannedMedia.Remove(chatId, file) ? "banMediaRemoved" : "banMediaNotBanned"), update);
            Service.LogCommand(update, message.Text);
        }
    }
}
