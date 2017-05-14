using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
#pragma warning disable CS4014
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "maxnl",  InGroupOnly = true, GroupAdminOnly = true)]
        public static void MaxNameLength(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            int chars;
            if (Int32.TryParse(args[1], out chars))
            {
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:antinamelengthsettings", "maxlength", chars);
                Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                return;
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
        }
        [Command(Trigger = "maxtl", InGroupOnly = true, GroupAdminOnly = true)]
        public static void MaxTextLength(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            int chars;
            if (Int32.TryParse(args[1], out chars))
            {
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:antitextlengthsettings", "maxlength", chars);
                Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                return;
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
        }
        [Command(Trigger = "maxl", InGroupOnly = true, GroupAdminOnly = true)]
        public static void MaxLines(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            int chars;
            if (Int32.TryParse(args[1], out chars))
            {
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:antitextlengthsettings", "maxlines", chars);
                Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                return;
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
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

        [Command(Trigger = "setrules", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetRules(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.SendReply(input, update);
                    Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:rules", input);
                   var res = Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "RulesSet"));
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "addrule", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AddRule(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var rules = Redis.db.StringGetAsync($"chat:{update.Message.Chat.Id}:rules").Result;
                    var result = Bot.SendReply(input, update);
                    Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:rules", $"{rules}\n{input}");
                    var res = Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "RulesSet"));
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

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

        [Command(Trigger = "setabout", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetAbout(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.SendReply(input, update);
                    Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:about", input);
                    var res = Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "AboutSet"));
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
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

        [Command(Trigger = "user", InGroupOnly = true, GroupAdminOnly = true)]
        public static void User(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                if (userid == Bot.Me.Id)
                    return;
                var userMenu = new Menu(2);
                userMenu.Buttons = new List<InlineButton>
                {
                    new InlineButton(Methods.GetLocaleString(lang, "removeWarn"), $"userbuttonremwarns:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "ban"), $"userbuttonbanuser:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "Warn"), $"userbuttonwarnuser:{update.Message.Chat.Id}:{userid}")
                };

                var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
                Bot.SendReply(text, update, Key.CreateMarkupFromMenu(userMenu));
            }
            catch (Exception e)
            {
                if (e.Message.Equals("UnableToResolveId"))
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "UnableToGetID"), update);
                }
                else
                {
                    Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang);
                }
            }


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

        [Command(Trigger = "extra", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Extra(Update update, string[] args)
        {
            if (args[1] == null)
                return;
            if (!args[1].StartsWith("#"))
                return;
			
			int splitindex = 0;
			if (args[1].Contains(" ") && args[1].Contains("\n"))
			{
				splitindex = args[1].IndexOf(" ") < args[1].IndexOf("\n")
					? args[1].IndexOf(" ")
					: args[1].IndexOf("\n");
			}
			else if (args[1].Contains(" "))
			{
				splitindex = args[1].IndexOf(" ");
			}
			else if (args[1].Contains("\n"))
			{
				splitindex = args[1].IndexOf("\n");
			}
			else if (update.Message.ReplyToMessage == null) return;
			
            var words = splitindex != 0
                ? new[] {args[1].Substring(1, splitindex).Trim(), args[1].Substring(splitindex + 1)}
                : new[] {args[1].Substring(1).Trim(), null};
            words[0] = $"#{words[0]}";            
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (update.Message.ReplyToMessage != null && words[1] == null)
            {
                var fileId = Methods.GetMediaId(update.Message.ReplyToMessage);
                if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.Text))
                {
                    string text = update.Message.ReplyToMessage.Text;
                    try
                    {
                        var res = Bot.SendReply(text, update);
                        var result = res;
                        var hash = $"chat:{update.Message.Chat.Id}:extra";
                        Redis.db.HashDeleteAsync($"{hash}:{words[0]}", "mediaid");
                        Redis.db.HashSetAsync(hash, words[0], text);
                        var resulted = Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                            Methods.GetLocaleString(lang, "extraMediaSaved", words[0]));
                    }
                    catch (ApiRequestException e)
                    {
                        if (e.ErrorCode.Equals(118))
                        {
                            Bot.SendReply(
                                Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                update);
                        }
                        else if (e.ErrorCode.Equals(112))
                        {
                            Bot.SendReply(
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
                    var type = Methods.GetMediaType(update.Message.ReplyToMessage);
                    if (!string.IsNullOrEmpty(type))
                    {
                        var toSave = $"###file_id!{type}###:{fileId}";
                        Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:extra", words[0], toSave);
                        Bot.Send(Methods.GetLocaleString(lang, "extraMediaSaved", words[0]), update);
                        return;
                    }
                    return;
                }
                return;
            }
            else if (update.Message.ReplyToMessage != null && words[1] != null)
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
                            var result = res;
                            var hash = $"chat:{update.Message.Chat.Id}:extra";
                            Redis.db.HashSetAsync($"{hash}:{words[0]}", "mediaid", toSave);
                            Redis.db.HashSetAsync(hash, words[0], text);
                            var resulted = Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                                Methods.GetLocaleString(lang, "extraMediaSaved", words[0]));
                        }
                        catch (ApiRequestException e)
                        {
                            if (e.ErrorCode.Equals(118))
                            {
                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                            }
                            else if (e.ErrorCode.Equals(112))
                            {
                                Bot.SendReply(
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
                            Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:extra", words[0], toSave);
                            var hash = $"chat:{update.Message.Chat.Id}:extra";
                            Redis.db.HashSetAsync($"{hash}:{words[0]}", "caption", words[1]);
                            Bot.Send(Methods.GetLocaleString(lang, "extraMediaSaved", words[0]), update);
                        }
                        catch (ApiRequestException e)
                        {
                            if (e.ErrorCode.Equals(118))
                            {
                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                            }
                            else if (e.ErrorCode.Equals(112))
                            {
                                Bot.SendReply(
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
                    var result = res;
                    var hash = $"chat:{update.Message.Chat.Id}:extra";
                    Redis.db.HashDeleteAsync($"{hash}:{words[0]}", "mediaid");
                    Redis.db.HashSetAsync(hash, words[0], text);
                    var resulted =  Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "extraSaved", words[0]));
                }
                catch (ApiRequestException e)
                {
                    if (e.ErrorCode.Equals(118))
                    {
                        Bot.SendReply(
                            Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                            update);
                    }
                    else if (e.ErrorCode.Equals(112))
                    {
                        Bot.SendReply(
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
        public static void ExtraList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:extra";
            var commands = Redis.db.HashKeysAsync(hash).Result;

            if (commands.Length == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "noExtra"), update);
            }
            else
            {
                var text = string.Join("\n", commands.ToList());
                Bot.SendReply(Methods.GetLocaleString(lang, "extraList", text), update);
            }
        }

        [Command(Trigger = "extradel", InGroupOnly = true, GroupAdminOnly = true)]
        public static void ExtraDelete(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (args[1] != null)
            {
                var hash = $"chat:{update.Message.Chat.Id}:extra";
                var res = Redis.db.HashDeleteAsync(hash, args[1]);
                if (res.Result)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "extraDeleted"), update);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "noExtra"), update);
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

        [Command(Trigger = "setlink", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetLink(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var link = "";
            if (update.Message.Chat.Username != null)
            {
                link = $"https://t.me/{update.Message.Chat.Username}";
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}links", "link", link);
                Bot.SendReply(Methods.GetLocaleString(lang, "linkSet"), update);
            }
            else
            {
                if (args.Length == 2)
                {
                    link = args[1];
                    Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}links", "link", link);
                    Bot.SendReply(Methods.GetLocaleString(lang, "linkSet"), update);
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "NoLinkInputed"), update);
                }
            }
        }

        [Command(Trigger = "status", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Status(Update update, string[] args)
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
                    if (!string.IsNullOrEmpty(args[1]))
                    {
                        if (args[1].StartsWith("@"))
                            userId = Methods.ResolveIdFromusername(args[1], chatId);
                    }
                }
                if (userId > 0)
                {
                    var user = Bot.Api.GetChatMemberAsync(chatId, userId).Result;
                    var status = user.Status;
                    var name = user.User.FirstName;
                    if (user.User.Username != null)
                        name = $"{name} - @{user.User.Username}";
                    if (update.Message.Chat.Type == ChatType.Group && Methods.IsBanned(chatId, userId))
                        status = ChatMemberStatus.Kicked;
                    var reason = Redis.db.HashGetAsync($"chat:{chatId}:bannedlist:{userId}", "why").Result;
                    if (!reason.IsNullOrEmpty && status == ChatMemberStatus.Kicked)
                    {
                        
                        Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}", name, Methods.GetLocaleString(lang, "bannedFor", reason)), update);
                    }
                    else
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}", name,""), update);
                    }
                }
            }
            else if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
                var user = Bot.Api.GetChatMemberAsync(chatId, userId).Result;
                var status = user.Status;
                var name = user.User.FirstName;
                if (user.User.Username != null)
                    name = $"{name} - @{user.User.Username}";
                if (update.Message.Chat.Type == ChatType.Group && Methods.IsBanned(chatId, userId))
                    status = ChatMemberStatus.Kicked;
                var reason = Redis.db.HashGetAsync($"chat:{chatId}:bannedlist:{userId}", "why").Result;
                if (!reason.IsNullOrEmpty && status == ChatMemberStatus.Kicked)
                {

                    Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}", name, Methods.GetLocaleString(lang, "bannedFor", reason)), update);
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}", name, ""), update);
                }
            }

        }

        [Command(Trigger = "welcome", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetWelcome(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            if (args.Length == 2)
            {
                if (!string.IsNullOrEmpty(args[1]))
                {
                    Redis.db.HashSetAsync($"chat:{chatId}:welcome", "hasmedia", "false");
                    if (update.Message.ReplyToMessage != null)
                    {
                        var repliedTo = Methods.GetMediaType(update.Message.ReplyToMessage);
                        if (repliedTo.Equals("gif"))
                        {
                            var fileId = Methods.GetMediaId(update.Message.ReplyToMessage);
                            Redis.db.HashSetAsync($"chat:{chatId}:welcome", "hasmedia", "true");
                            Redis.db.HashSetAsync($"chat:{chatId}:welcome", "media", fileId);
                            Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet"), update.Message);
                        }
                    }
                    else if (args[1].Equals("a"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "a");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet"), update.Message);
                    }
                    else if (args[1].Equals("r"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "r");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet"), update.Message);
                    }
                    else if (args[1].Equals("m"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "m");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet"), update.Message);
                    }
                    else if (args[1].Equals("ar") || args[1].Equals("ra"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "ra");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet"), update.Message);
                    }
                    else if (args[1].Equals("mr") || args[1].Equals("rm"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "rm");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet"), update.Message);
                    }
                    else if (args[1].Equals("am") || args[1].Equals("ma"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "am");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet"), update.Message);
                    }
                    else if (args[1].Equals("ram") || args[1].Equals("rma") || args[1].Equals("arm") || args[1].Equals("amr") || args[1].Equals("mra") || args[1].Equals("mar"))
                    {
                       Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "ram");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet"), update.Message);
                    }
                    else if (args[1].Equals("no"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "no");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeReset"), update.Message);
                    }
                    else
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "custom");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", args[1]);
                        try
                        {
                            var res = Bot.SendReply(args[1], update);
                            var result =  Bot.Api.EditMessageTextAsync(chatId, res.MessageId, Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName));
                        }
                        catch (AggregateException e)
                        {
                            Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                            Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "no");
                            Bot.SendReply(e.Message, update);
                        }
                    }
                }
                else if (update.Message.ReplyToMessage != null)
                {
                    var repliedTo = Methods.GetMediaType(update.Message.ReplyToMessage);
                    if (repliedTo.Equals("gif"))
                    {
                        var fileID = Methods.GetMediaId(update.Message.ReplyToMessage);
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "media");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", args[1]);
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "media", fileID);
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "hasmedia", true);
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update);
                    }
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "incorrectArgument"), update);
                }
            }            
        }

        [Command(Trigger = "disablewatch", InGroupOnly = true, GroupAdminOnly = true)]
        public static void DisbleMediaExcempt(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var userId = update.Message.From.Id;
            var chatId = update.Message.Chat.Id;
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            Redis.db.SetAddAsync($"chat:{chatId}:watch", userId);
            Bot.SendReply(Methods.GetLocaleString(lang, "off"), update);
        }
        [Command(Trigger = "enablewatch", InGroupOnly = true, GroupAdminOnly = true)]
        public static void EnableMediaExcempt(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var userId = update.Message.From.Id;
            var chatId = update.Message.Chat.Id;
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            Redis.db.SetRemoveAsync($"chat:{chatId}:watch", userId);
            Bot.SendReply(Methods.GetLocaleString(lang, "on"), update);
        }

        [Command(Trigger = "check", InGroupOnly = true, GroupAdminOnly = true)]
        public static void CheckGroupUser(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                try
                {
                    var userid = Methods.GetUserId(update, args);
                    var exists = Redis.db.HashGetAsync($"{update.Message.Chat.Id}:users:{userid}", "msgs").Result;
                    if (exists.HasValue)
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, "usergroupstatus", userid, exists), update);
                    }
                    else
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, "usergroupstatusnotseen", userid, exists), update);
                    }
                }
                catch (Exception e)
                {
                    Methods.SendError(e.Message, update.Message, lang);
                }
            }
            catch (AggregateException e)
            {
                Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
            }
        }

        [Command(Trigger = "elevate", InGroupOnly = true)]
        public static void ElevateUser(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                if (userid == Bot.Me.Id)
                    return;
                if (update.Message.From.Id == userid)
                    return;
                var chat = update.Message.Chat.Id;
                var role = Bot.Api.GetChatMemberAsync(chat, update.Message.From.Id);
                var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id.ToString()).Result;
                var upriv = Redis.db.SetContainsAsync($"chat:{chat}:deauth", update.Message.From.Id).Result;
                    var blocked = Redis.db.StringGetAsync($"chat:{chat}:blockList:{update.Message.From.Id}").Result;
                if ((role.Result.Status == ChatMemberStatus.Creator || priv))
                {
                    Redis.db.SetAddAsync($"chat:{chat}:mod", userid);
                    Redis.db.StringSetAsync($"chat:{chat}:adminses:{userid}", "true");
                    if (blocked.HasValue)
                        Redis.db.SetRemoveAsync($"chat:{chat}:blockList", userid);
                    Bot.SendReply(Methods.GetLocaleString(lang, "evlavated", userid, update.Message.From.Id), update);
                }                    
                else if (!upriv & blocked.HasValue == false & Methods.IsGroupAdmin(update))
                {
                     Redis.db.StringSetAsync($"chat:{chat}:adminses:{userid}", "true", TimeSpan.FromMinutes(30));
                     Redis.db.SetAddAsync($"chat:{chat}:modlog",
                        $"{Redis.db.HashGetAsync($"user:{userid}", "name").Result.ToString()} ({userid}) by {update.Message.From.FirstName} ({update.Message.From.Id}) at {System.DateTime.UtcNow} UTC");
                     Redis.db.StringSetAsync($"chat:{chat}:blockList:{userid}", userid, TimeSpan.FromMinutes(31));
                     Bot.SendReply(Methods.GetLocaleString(lang, "evlavated", userid, update.Message.From.Id), update);
                }
                Service.LogCommand(update, "elevate");

            }
            catch (Exception e)
            {
                if (e.Message.Equals("UnableToResolveId"))
                {
                     Bot.SendReply(Methods.GetLocaleString(lang, "UnableToGetID"), update);
                }
                else
                {
                    Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang);
                }
            }
        }

        [Command(Trigger = "deelevate", InGroupOnly = true)]
        public static void deElavateUser(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                if (userid == Bot.Me.Id)
                    return;
                var chat = update.Message.Chat.Id;
                var role = Bot.Api.GetChatMemberAsync(chat, update.Message.From.Id);
                var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
                if (role.Result.Status == ChatMemberStatus.Creator || priv)
                {
                    var set = Redis.db.StringSetAsync($"chat:{chat}:adminses:{userid}", "false").Result;
                     Redis.db.SetRemoveAsync($"chat:{chat}:mod", userid);
                     Bot.SendReply(Methods.GetLocaleString(lang, "devlavated", userid, update.Message.From.Id), update);
                    Service.LogCommand(update, "deelevate");
                }              
            }
            catch (Exception e)
            {
                if (e.Message.Equals("UnableToResolveId"))
                {
                     Bot.SendReply(Methods.GetLocaleString(lang, "UnableToGetID"), update);
                }
                else
                {
                    Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang);
                }
            }
        }

        [Command(Trigger = "elevatelog", GroupAdminOnly = true, InGroupOnly = true)]
        public static void ElevateLog(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var log = Redis.db.SetMembersAsync($"chat:{update.Message.Chat.Id}:modlog").Result.Select(e => e.ToString()).ToList();
                 Bot.SendReply($"{Methods.GetLocaleString(lang, "prevMods")} {string.Join("\n", log)}", update);      
        }

        [Command(Trigger = "auth", InGroupOnly = true)]
        public static void AuthUser(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            if (userid == Bot.Me.Id)
                return;
            var role = Bot.Api.GetChatMemberAsync(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if ((role.Result.Status == ChatMemberStatus.Creator || priv) || update.Message.From.Id == Constants.Devs[0])
            {
                var lang = Methods.GetGroupLanguage(update.Message).Doc;

                 Redis.db.SetAddAsync($"chat:{chat}:auth", userid);
                 Bot.SendReply(Methods.GetLocaleString(lang, "auth", userid, update.Message.From.Id), update);
            }
        }

        [Command(Trigger = "blockelevate", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Blockelevate(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            if (userid == Bot.Me.Id)
                return;
            var role = Bot.Api.GetChatMemberAsync(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator | priv)
            {
                var lang = Methods.GetGroupLanguage(update.Message).Doc;

                 Redis.db.SetAddAsync($"chat:{chat}:deauth", userid);
                 Bot.SendReply(Methods.GetLocaleString(lang, "auth", userid, update.Message.From.Id), update);
            }
        }

        [Command(Trigger = "deauth", InGroupOnly = true)]
        public static void deAuthUser(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            if (userid == Bot.Me.Id)
                return;
            var role = Bot.Api.GetChatMemberAsync(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator || priv)
            {
                var lang = Methods.GetGroupLanguage(update.Message).Doc;
                 Redis.db.SetRemoveAsync($"chat:{chat}:auth", userid);
                 Bot.SendReply(Methods.GetLocaleString(lang, "dauth", userid, update.Message.From.Id), update);
            }
        }

        [Command(Trigger = "resetSettings", InGroupOnly = true, GroupAdminOnly = true)]
        public static void resetSettings(Update update, string[] args)
        {
            Service.GenerateSettings(update.Message.Chat.Id);
            Bot.SendReply("Settings have been reset", update);
        }

        [Command(Trigger = "addlogchannel", InGroupOnly = true)]
        public static void AddLogChat(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var role = Bot.Api.GetChatMemberAsync(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator || priv)
            {
                long channelId;
                var lang = Methods.GetGroupLanguage(update.Message).Doc;
                if (long.TryParse(args[1], out channelId))
                {
                    Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:settings", "logchat", channelId);
                    Redis.db.SetAddAsync("logChatGroups", update.Message.Chat.Id);

                    Bot.SendReply(Methods.GetLocaleString(lang, "channelAdded"), update);
                }
                else if (update.Message.ReplyToMessage != null)
                {
                    if (update.Message.ReplyToMessage.ForwardFromChat != null)
                    {
                        channelId = update.Message.ReplyToMessage.ForwardFromChat.Id;
                        Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:settings", "logchat", channelId);
                        Redis.db.SetAddAsync("logChatGroups", update.Message.Chat.Id);

                        Bot.SendReply(Methods.GetLocaleString(lang, "channelAdded"), update);                   
                    }
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "notChannel"), update);
                }
            }
        }

        [Command(Trigger = "dellogchannel", InGroupOnly = true)]
        public static void DeleteLogChannel(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var role = Bot.Api.GetChatMemberAsync(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator || priv)
            {
                Redis.db.SetRemoveAsync("logChatGroups", update.Message.Chat.Id);
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
                                Bot.Api.SendVoiceAsync(update.Message.Chat.Id, fileId,
                                    replyToMessageId: repId);
                                break;
                            case "video":
                                if (!string.IsNullOrEmpty(caption))
                                {
                                    Bot.Api.SendVideoAsync(update.Message.Chat.Id, fileId, caption: caption,
                                        replyToMessageId: repId);
                                }
                                else
                                {
                                     Bot.Api.SendVideoAsync(update.Message.Chat.Id, fileId,
                                        replyToMessageId: repId);
                                }
                                break;
                            case "photo":
                                if (!string.IsNullOrEmpty(caption))
                                {
                                     Bot.Api.SendPhotoAsync(update.Message.Chat.Id, fileId, caption,
                                        replyToMessageId: repId);
                                }
                                else
                                {
                                     Bot.Api.SendPhotoAsync(update.Message.Chat.Id, fileId,
                                        replyToMessageId: repId);
                                }
                                break;
                            case "gif":
                                if (!string.IsNullOrEmpty(hasMedia))
                                {
                                     Bot.Api.SendDocumentAsync(update.Message.Chat.Id, fileId, caption,
                                        replyToMessageId: repId);
                                }
                                else
                                {
                                     Bot.Api.SendDocumentAsync(update.Message.Chat.Id, fileId,
                                        replyToMessageId: repId);
                                }
                                break;
                            default:
                                if (!string.IsNullOrEmpty(hasMedia))
                                {
                                     Bot.Api.SendDocumentAsync(update.Message.Chat.Id, fileId, text,
                                        replyToMessageId: repId);
                                }
                                else
                                {
                                     Bot.Api.SendDocumentAsync(update.Message.Chat.Id, fileId,
                                        replyToMessageId: repId);
                                }
                                break;
                        }
                    }
                    else if (!string.IsNullOrEmpty(hasMedia))
                    {
                         Bot.Api.SendDocumentAsync(update.Message.Chat.Id, hasMedia, text,
                            replyToMessageId: repId);
                    }
                    else
                    {
                         Bot.Api.SendDocumentAsync(update.Message.Chat.Id, hasMedia,
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

    public static partial class CallBacks
    {
        [Callback(Trigger = "userbuttonremwarns", GroupAdminOnly = true)]
        public static void UserButtonRemWarns(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var userId = args[2];
             Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:warns", userId);
             Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:mediawarn", userId);
             Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnsReset", call.From.FirstName));
        }

        [Callback(Trigger = "userbuttonbanuser", GroupAdminOnly = true)]
        public static void UserButtonsBanUser(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var userId = args[2];
             Methods.BanUser(call.Message.Chat.Id, int.Parse(userId), lang);
            var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{call.Message.Chat.Id}:tempbanned", userId).Result;
            if (isAlreadyTempbanned)
            {
                var all = Redis.db.HashGetAllAsync("tempbanned").Result;
                foreach (var mem in all)
                {
                    if ($"{call.Message.Chat.Id}:{userId}".Equals(mem.Value))
                    {
                         Redis.db.HashDeleteAsync("tempbanned", mem.Name);
                    }
                }
                 Redis.db.SetRemoveAsync($"chat:{call.Message.Chat.Id}:tempbanned", userId);
            }
            Methods.SaveBan(int.Parse(userId), "ban");
             Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "userBanned"));
        }

        [Callback(Trigger = "userbuttonwarnuser", GroupAdminOnly = true)]
        public static void UserButtonsWarnUser(CallbackQuery call, string[] args)
        {
            var userId = int.Parse(args[2]);
            var chatId = long.Parse(args[1]);
            if (Methods.IsGroupAdmin(userId, chatId))
            {
                return;
                
            }
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var num = Redis.db.HashIncrementAsync($"chat:{chatId}:warns", userId, 1).Result;
            var max = 3;
            int.TryParse(Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result, out max);
            lang = Methods.GetGroupLanguage(call.Message).Doc;
            if (num >= max)
            {
                var type = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").Result.HasValue
                    ? Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").ToString()
                    : "kick";
                if (type.Equals("ban"))
                {
                    try
                    {
                         var res = Bot.Api.KickChatMemberAsync(call.Message.Chat.Id, userId);
                         var result = Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, Methods.GetLocaleString(lang, "warnMaxBan", userId), parseMode: ParseMode.Html);
                    }
                    catch (AggregateException e)
                    {
                        Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", call.Message, lang);
                    }
                }
                else
                {
                    Methods.KickUser(call.Message.Chat.Id, userId, lang);
                    Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, Methods.GetLocaleString(lang, "warnMaxKick", userId), parseMode: ParseMode.Html);
                }
                 Redis.db.HashSetAsync($"chat:{chatId}:warns", userId, 0);
            }
            else
            {
                var text = Methods.GetLocaleString(lang, "warn", userId, num, max);
                var baseMenu = new List<InlineKeyboardButton>();
                baseMenu.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang, "resetWarn"),
                    $"resetwarns:{chatId}:{userId}"));
                baseMenu.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang, "removeWarn"),
                    $"removewarn:{chatId}:{userId}"));
                var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
                 Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, text, replyMarkup:menu, parseMode:ParseMode.Html);
            }
        }
    }
}
