using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;


namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "about", InGroupOnly = true)]
        public static void About(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            var text = Methods.GetAbout(chatId, lang);
            if (Methods.SendInPm(update.Message, "About"))
            {
                Bot.Send(text, update.Message.From.Id);
                Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                Bot.SendReply(text, update);
            }
        }
        [Command(Trigger = "rules", InGroupOnly = true)]
        public static void Rules(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var text = Methods.GetRules(update.Message.Chat.Id, lang);
            if (Methods.SendInPm(update.Message, "Rules"))
            {
                Bot.Send(text, update.Message.From.Id);
                Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                Bot.SendReply(text, update);
            }
        }
        [Command(Trigger = "adminlist", InGroupOnly = true)]
        public static void AdminList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var text = Methods.GetAdminList(update.Message, lang);
            var mods = Redis.db.SetMembersAsync($"chat:{update.Message.Chat.Id}:mod").Result.Select(e => ($"{Redis.db.HashGetAsync($"user:{e.ToString()}", "name").Result.ToString()} ({e.ToString()})")).ToList();
            if (mods.Count > 0)
            {
                text = $"{text}\n{Methods.GetLocaleString(lang, "currentMods")}{string.Join("\n", mods)}";
            }
            if (Methods.SendInPm(update.Message, "Modlist"))
            {
                Bot.Send(text, update.Message.From.Id);
                Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                Bot.SendReply(text, update);
            }
        }

        [Command(Trigger = "s", InGroupOnly = true, RequiresReply = true)]
        public static void SaveMessage(Update update, string[] args)
        {
            var msgID = update.Message.ReplyToMessage.MessageId;
            var saveTo = update.Message.From.Id;
            var chat = update.Message.Chat.Id;
            try
            {
                var res = Bot.Api.ForwardMessageAsync(saveTo, chat, msgID, disableNotification: true);
            }
            catch (AggregateException e)
            {
                var lang = Methods.GetGroupLanguage(update.Message).Doc;
                Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
            }
        }

        [Command(Trigger = "id")]
        public static void GetId(Update update, string[] args)
        {
            long id;
            string user = "";
            string name;
            if (update.Message.ReplyToMessage != null)
            {
                if (update.Message.ReplyToMessage.ForwardFrom != null)
                {
                    id = update.Message.ReplyToMessage.ForwardFrom.Id;
                    name = update.Message.ReplyToMessage.ForwardFrom.FirstName;
                    if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.ForwardFrom.LastName))
                        name = $"{name} {update.Message.ReplyToMessage.ForwardFrom.LastName}";
                    if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.ForwardFrom.Username))
                        user = update.Message.ReplyToMessage.ForwardFrom.Username;
                }
                else
                {
                    id = update.Message.ReplyToMessage.From.Id;
                    name = update.Message.ReplyToMessage.From.FirstName;
                    if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.From.LastName))
                        name = $"{name} {update.Message.ReplyToMessage.From.LastName}";
                    if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.From.Username))
                        user = update.Message.ReplyToMessage.From.Username;
                }
            }
            else
            {
                id = update.Message.Chat.Id;
                name = update.Message.Chat.Title;
                if (!string.IsNullOrEmpty(update.Message.Chat.Username))
                    user = update.Message.Chat.Username;
            }
            if (!string.IsNullOrEmpty(user))
            {
                try
                {
                    Bot.SendReply($"{id}\n{name}\n@{user}", update.Message);
                }
                catch (AggregateException e)
                {
                    var lang = Methods.GetGroupLanguage(update.Message).Doc;
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                try
                {
                    Bot.SendReply($"{id}\n{name}", update.Message);
                }
                catch (AggregateException e)
                {
                    var lang = Methods.GetGroupLanguage(update.Message).Doc;
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
        }

        [Command(Trigger = "support")]
        public static void Support(Update update, string[] args)
        {
            int msgToReplyTo;
            if (update.Message.ReplyToMessage != null)
            {
                msgToReplyTo = update.Message.ReplyToMessage.MessageId;
            }
            else
            {
                msgToReplyTo = update.Message.MessageId;
            }
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var text = Methods.GetLocaleString(lang, "Support");
            Bot.SendReply(text, update.Message.Chat.Id, msgToReplyTo);
        }

        [Command(Trigger = "me")]
        public static void Me(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                var userid = update.Message.From.Id;
                var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
                Bot.Send(text, update.Message.From.Id);
                if (update.Message.Chat.Type != ChatType.Private)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
                }
            }
            catch (ApiRequestException e)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "startMe"), update);
            }
            catch (AggregateException e)
            {
                Methods.SendError($"{e.InnerExceptions[0].Message}\n{e.StackTrace}", update.Message, lang);
            }
        }

        [Command(Trigger = "link", InGroupOnly = true)]
        public static void Link(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var link = Redis.db.HashGetAsync($"chat:{update.Message.Chat.Id}links", "link").Result;
            if (link.Equals("no") || link.IsNullOrEmpty)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "linkMissing"), update);
            }
            else
            {
                Bot.SendReply($"<a href=\"{link}\">{update.Message.Chat.Title}</a>", update);
            }
        }

        public static void SendExtra(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:extra";
            var text = Redis.db.HashGetAsync(hash, args[0]).Result;
            if (!text.HasValue)
                return;
            var fileId = "";
            var specialMethod = "";
            if (text.ToString().Contains("###file_id!"))
            {
                var split = text.ToString()
                    .Replace("###file_id!", "")
                    .Split(new[] { "###:" }, StringSplitOptions.RemoveEmptyEntries);
                fileId = split[1];
                specialMethod = split[0];

            }
            var hasMedia = Redis.db.HashGetAsync($"{hash}:{args[0]}", "mediaid").Result;
            var repId = update.Message.MessageId;
            if (update.Message.ReplyToMessage != null)
            {
                repId = update.Message.ReplyToMessage.MessageId;
            }
            if (string.IsNullOrEmpty(fileId) && string.IsNullOrEmpty(hasMedia))
            {
                Bot.SendReply(text, update.Message.Chat.Id, repId);
            }
            else
            {
                try
                {
                    if (!string.IsNullOrEmpty(specialMethod))
                    {
                        var caption = Redis.db.HashGetAsync($"{hash}:{args[0]}", "caption").Result;
                        switch (specialMethod)
                        {
                            case "voice":
                                Bot.Api.SendVoiceAsync(update.Message.Chat.Id, new FileToSend(fileId),
                                    replyToMessageId: repId);
                                break;
                            case "video":
                                if (!string.IsNullOrEmpty(caption))
                                {
                                    Bot.Api.SendVideoAsync(update.Message.Chat.Id, new FileToSend(fileId), caption: caption,
                                        replyToMessageId: repId);
                                }
                                else
                                {
                                    Bot.Api.SendVideoAsync(update.Message.Chat.Id, new FileToSend(fileId),
                                        replyToMessageId: repId);
                                }
                                break;
                            case "photo":
                                if (!string.IsNullOrEmpty(caption))
                                {
                                    Bot.Api.SendPhotoAsync(update.Message.Chat.Id, new FileToSend(fileId), caption,
                                        replyToMessageId: repId);
                                }
                                else
                                {
                                    Bot.Api.SendPhotoAsync(update.Message.Chat.Id, new FileToSend(fileId),
                                        replyToMessageId: repId);
                                }
                                break;

                            case "videoNote":
                                    Bot.Api.SendVideoNoteAsync(update.Message.Chat.Id, new FileToSend(fileId),
                                        replyToMessageId: repId);
                                break;
                            case "gif":
                                if (!string.IsNullOrEmpty(hasMedia))
                                {
                                    Bot.Api.SendDocumentAsync(update.Message.Chat.Id, new FileToSend(fileId), caption,
                                        replyToMessageId: repId);
                                }
                                else
                                {
                                    Bot.Api.SendDocumentAsync(update.Message.Chat.Id, new FileToSend(fileId),
                                        replyToMessageId: repId);
                                }
                                break;
                            default:
                                if (!string.IsNullOrEmpty(hasMedia))
                                {
                                    Bot.Api.SendDocumentAsync(update.Message.Chat.Id, new FileToSend(fileId), text,
                                        replyToMessageId: repId);
                                }
                                else
                                {
                                    Bot.Api.SendDocumentAsync(update.Message.Chat.Id, new FileToSend(fileId),
                                        replyToMessageId: repId);
                                }
                                break;
                        }
                    }
                    else if (!string.IsNullOrEmpty(hasMedia))
                    {
                        Bot.Api.SendDocumentAsync(update.Message.Chat.Id, new FileToSend(hasMedia), text,
                            replyToMessageId: repId);
                    }
                    else
                    {
                        Bot.Api.SendDocumentAsync(update.Message.Chat.Id, new FileToSend(hasMedia),
                            replyToMessageId: repId);
                    }
                }
                catch (ApiRequestException e)
                {
                    Methods.SendError($"Extra corrupted please recreate it, Error message for dev: {e.Message}", update.Message, lang);
                    // Bot.Send($"@falconza #theOne shit happened\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
                }
            }
        }
    }
}
