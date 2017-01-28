using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "rules", InGroupOnly = true)]
        public static async Task Rules(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var text = Methods.GetRules(update.Message.Chat.Id, lang);
            if (Methods.SendInPm(update.Message, "Rules"))
            {
                await Bot.Send(text, update.Message.From.Id);
                await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                await Bot.SendReply(text, update);
            }
        }

        [Command(Trigger = "setrules", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task SetRules(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.SendReply(input, update);
                    await Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:rules", input);
                    await Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.Result.MessageId,
                        Methods.GetLocaleString(lang, "RulesSet"));
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "about", InGroupOnly = true)]
        public static async Task About(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            var text = Methods.GetAbout(chatId, lang);
            if (Methods.SendInPm(update.Message, "About"))
            {
                await Bot.Send(text, update.Message.From.Id);
                await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                await Bot.SendReply(text, update);
            }
        }

        [Command(Trigger = "setabout", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task SetAbout(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.SendReply(input, update);
                    await Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:about", input);
                    await Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.Result.MessageId,
                        Methods.GetLocaleString(lang, "AboutSet"));
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "adminlist", InGroupOnly = true)]
        public static async Task AdminList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var text = Methods.GetAdminList(update.Message, lang);
            if (Methods.SendInPm(update.Message, "Modlist"))
            {
                await Bot.Send(text, update.Message.From.Id);
                await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                await Bot.SendReply(text, update);
            }
        }

        [Command(Trigger = "s", InGroupOnly = true, RequiresReply = true)]
        public static async Task SaveMessage(Update update, string[] args)
        {
            var msgID = update.Message.ReplyToMessage.MessageId;
            var saveTo = update.Message.From.Id;
            var chat = update.Message.Chat.Id;
            try
            {
                await Bot.Api.ForwardMessageAsync(saveTo, chat, msgID, disableNotification: true);
            }
            catch (AggregateException e)
            {
                var lang = Methods.GetGroupLanguage(update.Message).Doc;
                Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
            }
        }

        [Command(Trigger = "id")]
        public static async Task GetId(Update update, string[] args)
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
                    await Bot.SendReply($"{id}\n{name}\n@{user}", update.Message);
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
                    await Bot.SendReply($"{id}\n{name}", update.Message);
                }
                catch (AggregateException e)
                {
                    var lang = Methods.GetGroupLanguage(update.Message).Doc;
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
        }

        [Command(Trigger = "support")]
        public static async Task Support(Update update, string[] args)
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
            await Bot.SendReply(Methods.GetLocaleString(lang, "Support"), update.Message.Chat.Id, msgToReplyTo);
        }

        [Command(Trigger = "user", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task User(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                var userMenu = new Menu(2);
                userMenu.Buttons = new List<InlineButton>
                {
                    new InlineButton(Methods.GetLocaleString(lang, "removeWarn"), $"userbuttonremwarns:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "ban"), $"userbuttonbanuser:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "Warn"), $"userbuttonwarnuser:{userid}")
                };

                var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
                await Bot.SendReply(text, update, Key.CreateMarkupFromMenu(userMenu));
            }
            catch (Exception e)
            {
                if (e.Message.Equals("UnableToResolveId"))
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "UnableToGetID"), update);
                }
                else
                {
                    Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang);
                }
            }


        }

        [Command(Trigger = "me")]
        public static async Task Me(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                var userid = update.Message.From.Id;
                var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
                await Bot.Send(text, update.Message.From.Id);
                if (update.Message.Chat.Type != ChatType.Private)
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
                }
            }
            catch (ApiRequestException e)
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "startMe"), update);
            }
            catch (AggregateException e)
            {
                Methods.SendError($"{e.InnerExceptions[0].Message}\n{e.StackTrace}", update.Message, lang);
            }
        }

        [Command(Trigger = "extra", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task Extra(Update update, string[] args)
        {
            if (args[1] == null)
                return;
            if (!args[1].StartsWith("#"))
                return;
            var words = args[1].Contains(" ")
                ? new[] {args[1].Substring(1, args[1].IndexOf(" ")).Trim(), args[1].Substring(args[1].IndexOf(" ") + 1)}
                : new[] {args[1].Substring(1).Trim(), null};
            words[0] = $"#{words[0]}";
            if (words[1] == null && update.Message.ReplyToMessage == null)
                return;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (update.Message.ReplyToMessage != null && words[1] == null)
            {
                var fileId = Methods.GetMediaId(update.Message.ReplyToMessage);
                if (!string.IsNullOrEmpty(fileId))
                {
                    var type = Methods.GetMediaType(update.Message.ReplyToMessage);
                    if (!string.IsNullOrEmpty(type))
                    {
                        var toSave = $"###file_id!{type}###:{fileId}";
                        await Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:extra", words[0], toSave);
                        await Bot.Send(Methods.GetLocaleString(lang, "extraSaved", words[0]), update);
                        return;
                    }
                    return;
                }
                return;
            }
            if (update.Message.ReplyToMessage != null && words[1] != null)
            {
                var type = Methods.GetMediaType(update.Message.ReplyToMessage);
                if (!string.IsNullOrEmpty(type))
                {
                    if (type.Equals("gif"))
                    {
                        var toSave = update.Message.ReplyToMessage.Document.FileId;
                        string text = words[1];
                        try
                        {
                            var res = Bot.SendReply(text, update);
                            var result = res.Result;
                            var hash = $"chat:{update.Message.Chat.Id}:extra";
                            await Redis.db.HashSetAsync($"{hash}:{words[0]}", "mediaid", toSave);
                            await Redis.db.HashSetAsync(hash, words[0], text);
                            await Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                                Methods.GetLocaleString(lang, "extraSaved", words[0]));
                        }
                        catch (ApiRequestException e)
                        {
                            if (e.ErrorCode.Equals(118))
                            {
                                await Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                            }
                            else if (e.ErrorCode.Equals(112))
                            {
                                await Bot.SendReply(
                                    Methods.GetLocaleString(lang, "markdownBroken"), update);
                            }
                            else
                            {
                                Methods.SendError($"{e.ErrorCode}\n\n{e.Message}", update.Message, lang);
                            }
                        }
                    }
                    else
                    {
                        string text = words[1];
                        try
                        {
                            var fileId = Methods.GetMediaId(update.Message.ReplyToMessage);
                            var toSave = $"###file_id!{type}###:{fileId}";
                            await Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:extra", words[0], toSave);
                            var hash = $"chat:{update.Message.Chat.Id}:extra";
                            await Redis.db.HashSetAsync($"{hash}:{words[0]}", "caption", words[1]);
                            await Bot.Send(Methods.GetLocaleString(lang, "extraSaved", words[0]), update);
                        }
                        catch (ApiRequestException e)
                        {
                            if (e.ErrorCode.Equals(118))
                            {
                                await Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                            }
                            else if (e.ErrorCode.Equals(112))
                            {
                                await Bot.SendReply(
                                    Methods.GetLocaleString(lang, "markdownBroken"), update);
                            }
                            else
                            {
                                Methods.SendError($"{e.ErrorCode}\n\n{e.Message}", update.Message, lang);
                            }
                        }
                    }
                }
            }
            else
            {
                string text = words[1];
                try
                {
                    var res = Bot.SendReply(text, update);
                    var result = res.Result;
                    var hash = $"chat:{update.Message.Chat.Id}:extra";
                    await Redis.db.HashDeleteAsync($"{hash}:{words[0]}", "mediaid");
                    await Redis.db.HashSetAsync(hash, words[0], text);
                    await Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "extraSaved", words[0]));
                }
                catch (ApiRequestException e)
                {
                    if (e.ErrorCode.Equals(118))
                    {
                        await Bot.SendReply(
                            Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                            update);
                    }
                    else if (e.ErrorCode.Equals(112))
                    {
                        await Bot.SendReply(
                            Methods.GetLocaleString(lang, "markdownBroken"), update);
                    }
                    else
                    {
                        Methods.SendError($"{e.ErrorCode}\n\n{e.Message}", update.Message, lang);
                    }
                }

            }
        }

        [Command(Trigger = "extralist", InGroupOnly = true)]
        public static async Task ExtraList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:extra";
            var commands = Redis.db.HashKeysAsync(hash).Result;

            if (!commands[0].HasValue)
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "noExtra"), update);
            }
            else
            {
                var text = string.Join("\n", commands.ToList());
                await Bot.SendReply(Methods.GetLocaleString(lang, "extraList", text), update);
            }
        }

        [Command(Trigger = "extradel", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task ExtraDelete(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (args[1] != null)
            {
                var hash = $"chat:{update.Message.Chat.Id}:extra";
                var res = Redis.db.HashDeleteAsync(hash, args[1]);
                if (res.Result)
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "extraDeleted"), update);
                }
            }
            else
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "noExtra"), update);
            }
        }

        [Command(Trigger = "link", InGroupOnly = true)]
        public static async Task Link(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var link = Redis.db.HashGetAsync($"chat:{update.Message.Chat.Id}links", "link").Result;
            if (link.Equals("no") || link.IsNullOrEmpty)
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "linkMissing"), update);
            }
            else
            {
                await Bot.SendReply(link.ToString(), update);
            }
        }

        [Command(Trigger = "setlink", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task SetLink(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var link = "";
            if (update.Message.Chat.Username != null)
            {
                link = $"https://t.me/{update.Message.Chat.Username}";
            }
            else
            {
                if (args.Length == 2)
                {
                    link = args[1];
                    await Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}links", "link", link);
                    await Bot.SendReply(Methods.GetLocaleString(lang, "linkSet"), update);
                }
                else
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "NoLinkInputed"), update);
                }
            }
        }

        [Command(Trigger = "status", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task Status(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var userId = 0;
            var chatId = update.Message.Chat.Id;
            if (args.Length == 2)
            {
                if (int.TryParse(args[1], out userId))
                {

                }
                else
                {
                    userId = Methods.ResolveIdFromusername(args[1], chatId);
                }
                if (userId > 0)
                {
                    var user = Bot.Api.GetChatMemberAsync(chatId, userId).Result;
                    var status = user.Status;
                    var name = user.User.FirstName;
                    if (user.User.Username != null)
                        name = $"@{user.User.Username}";
                    if (update.Message.Chat.Type == ChatType.Group && Methods.IsBanned(chatId, userId))
                        status = ChatMemberStatus.Kicked;
                    var reason = Redis.db.HashGetAsync($"chat:{chatId}:bannedlist:{userId}", "why").Result;
                    if (!reason.IsNullOrEmpty)
                    {
                        name = $"{name}\t{Methods.GetLocaleString(lang, "bannedFor")}";
                    }
                    await Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}"), update);
                }
            }

        }

        public static async Task SendExtra(Update update, string[] args)
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
                    .Split(new[] {"###:"}, StringSplitOptions.RemoveEmptyEntries);
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
                await Bot.SendReply(text, update.Message.Chat.Id, repId);
            }
            else
            {
                if (!string.IsNullOrEmpty(specialMethod))
                {
                    var caption = Redis.db.HashGetAsync($"{hash}:{args[0]}", "caption").Result;
                    switch (specialMethod)
                    {
                        case "voice":
                            await Bot.Api.SendVoiceAsync(update.Message.Chat.Id, fileId,
                                replyToMessageId: repId);
                            break;
                        case "video":
                            if (!string.IsNullOrEmpty(caption))
                            {
                                await Bot.Api.SendVideoAsync(update.Message.Chat.Id, fileId, caption: caption,
                                    replyToMessageId: repId);
                            }
                            else
                            {
                                await Bot.Api.SendVideoAsync(update.Message.Chat.Id, fileId,
                                    replyToMessageId: repId);
                            }
                            break;
                        case "photo":
                            if (!string.IsNullOrEmpty(caption))
                            {
                                await Bot.Api.SendPhotoAsync(update.Message.Chat.Id, fileId, caption,
                                    replyToMessageId: repId);
                            }
                            else
                            {
                                await Bot.Api.SendPhotoAsync(update.Message.Chat.Id, fileId,
                                    replyToMessageId: repId);
                            }
                            break;
                        case "gif":
                            if (!string.IsNullOrEmpty(hasMedia))
                            {
                                await Bot.Api.SendDocumentAsync(update.Message.Chat.Id, fileId, caption,
                                    replyToMessageId: repId);
                            }
                            else
                            {
                                await Bot.Api.SendDocumentAsync(update.Message.Chat.Id, fileId,
                                    replyToMessageId: repId);
                            }
                            break;
                        default:
                            if (!string.IsNullOrEmpty(hasMedia))
                            {
                                await Bot.Api.SendDocumentAsync(update.Message.Chat.Id, fileId, text,
                                    replyToMessageId: repId);
                            }
                            else
                            {
                                await Bot.Api.SendDocumentAsync(update.Message.Chat.Id, fileId,
                                    replyToMessageId: repId);
                            }
                            break;
                    }
                }
                else if (!string.IsNullOrEmpty(hasMedia))
                {
                    await Bot.Api.SendDocumentAsync(update.Message.Chat.Id, hasMedia, text,
                        replyToMessageId: repId);
                }
                else
                {
                    await Bot.Api.SendDocumentAsync(update.Message.Chat.Id, hasMedia,
                        replyToMessageId: repId);
                }
            }
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "userbuttonremwarns", GroupAdminOnly = true)]
        public static async Task UserButtonRemWarns(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var userId = args[1];
            await Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:warns", userId);
            await Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:mediawarn", userId);
            await Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnsReset", call.From.FirstName));
        }

        [Callback(Trigger = "userbuttonbanuser", GroupAdminOnly = true)]
        public static async Task UserButtonsBanUser(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var userId = args[1];
            await Methods.BanUser(call.Message.Chat.Id, int.Parse(userId), lang);
            var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{call.Message.Chat.Id}:tempbanned", userId).Result;
            if (isAlreadyTempbanned)
            {
                var all = Redis.db.HashGetAllAsync("tempbanned").Result;
                foreach (var mem in all)
                {
                    if ($"{call.Message.Chat.Id}:{userId}".Equals(mem.Value))
                    {
                        await Redis.db.HashDeleteAsync("tempbanned", mem.Name);
                    }
                }
                await Redis.db.SetRemoveAsync($"chat:{call.Message.Chat.Id}:tempbanned", userId);
            }
            Methods.SaveBan(int.Parse(userId), "ban");
        }

        [Callback(Trigger = "userbuttonwarnuser", GroupAdminOnly = true)]
        public static async Task UserButtonsWarnUser(CallbackQuery call, string[] args)
        {
            if (Methods.IsGroupAdmin(call.Message.ReplyToMessage.From.Id, call.Message.Chat.Id))
                return;
            var userId = args[1];
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var num = Redis.db.HashIncrementAsync($"chat:{call.Message.Chat.Id}:warns", userId, 1).Result;
            var max = 3;
            int.TryParse(Redis.db.HashGetAsync($"chat:{call.Message.Chat.Id}:warnsettings", "max").Result, out max);
            lang = Methods.GetGroupLanguage(call.Message).Doc;
            if (num >= max)
            {
                var type = Redis.db.HashGetAsync($"chat:{call.Message.Chat.Id}:warnsettings", "type").Result.HasValue
                    ? Redis.db.HashGetAsync($"chat:{call.Message.Chat.Id}:warnsettings", "type").ToString()
                    : "kick";
                if (type.Equals("ban"))
                {
                    try
                    {
                        await Bot.Api.KickChatMemberAsync(call.Message.Chat.Id, int.Parse(userId));
                        var name = Methods.GetNick(call.Message, args);
                        await Bot.SendReply(Methods.GetLocaleString(lang, "warnMaxBan", name), call.Message);
                        await Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId, "");
                    }
                    catch (AggregateException e)
                    {
                        Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", call.Message, lang);
                    }
                }
                else
                {
                    await Methods.KickUser(call.Message.Chat.Id, int.Parse(userId), lang);
                    var name = Methods.GetNick(call.Message, args);
                    await Bot.SendReply(Methods.GetLocaleString(lang, "warnMaxKick", name), call.Message);
                    await Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId, "");
                }
            }
            else
            {
                var diff = max - num;
                var text = Methods.GetLocaleString(lang, "warn", Methods.GetNick(call.Message, args), num, max);
                var baseMenu = new List<InlineKeyboardButton>();
                baseMenu.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang, "resetWarn"),
                    $"resetwarns:{call.Message.ReplyToMessage.From.Id}"));
                baseMenu.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang, "removeWarn"),
                    $"removewarn:{call.Message.ReplyToMessage.From.Id}"));
                var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
                await Bot.Send(text, call.Message.Chat.Id, customMenu: menu);
                await Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId, "");
            }
        }
    }
}