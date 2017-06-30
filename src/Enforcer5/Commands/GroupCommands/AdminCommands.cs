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
       
        [Command(Trigger = "setrules", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetRules(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.SendReply(input, update);
                    Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:rules", input);
                   var res = Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "RulesSet"));
                    Service.LogCommand(update, update.Message.Text);
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
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
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
                    Service.LogCommand(update, update.Message.Text);
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

        [Command(Trigger = "setabout", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetAbout(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.SendReply(input, update);
                    Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:about", input);
                    var res = Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "AboutSet"));
                    Service.LogCommand(update, update.Message.Text);
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
      
        [Command(Trigger = "user", InGroupOnly = true, GroupAdminOnly = true)]
        public static void User(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                if (userid == Bot.Me.Id)
                    return;
                var userMenu = new Menu(2);
                userMenu.Buttons = new List<InlineButton>
                {
                    new InlineButton(Methods.GetLocaleString(lang, "removeWarn"), $"userbuttonremwarns:{update.Message.Chat.Id}:{userid}"),                    
                    new InlineButton(Methods.GetLocaleString(lang, "resetWarn"), $"userbuttonresetwarn:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "ban"), $"userbuttonbanuser:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "Warn"), $"userbuttonwarnuser:{update.Message.Chat.Id}:{userid}"),
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
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
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
                        Service.LogCommand(update, update.Message.Text);
                    }
                    catch (AggregateException e)
                    {

                        if (e.Message.Contains("message is too long"))
                        {
                      
                        Bot.SendReply(
                                Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                update);
                        }
                        else if (e.Message.Contains("Unsupported start tag"))
                        {
                            Bot.SendReply(
                                Methods.GetLocaleString(lang, "markdownBroken"), update);
                        }
                        else
                        {
                            Methods.SendError($"{e.Message}", update.Message, lang);
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
                        Service.LogCommand(update, update.Message.Text);
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
                            Service.LogCommand(update, update.Message.Text);
                        }
                        catch (AggregateException e)
                        {

                            if (e.Message.Contains("message is too long"))
                            {

                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                            }
                            else if (e.Message.Contains("Unsupported start tag"))
                            {
                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "markdownBroken"), update);
                            }
                            else
                            {
                                Methods.SendError($"{e.Message}", update.Message, lang);
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
                            Service.LogCommand(update, update.Message.Text);
                        }
                        catch (AggregateException e)
                        {

                            if (e.Message.Contains("message is too long"))
                            {

                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                            }
                            else if (e.Message.Contains("Unsupported start tag"))
                            {
                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "markdownBroken"), update);
                            }
                            else
                            {
                                Methods.SendError($"{e.Message}", update.Message, lang);
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
                    Service.LogCommand(update, update.Message.Text);
                }
                catch (AggregateException e)
                {

                    if (e.Message.Contains("message is too long"))
                    {

                        Bot.SendReply(
                            Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                            update);
                    }
                    else if (e.Message.Contains("Unsupported start tag"))
                    {
                        Bot.SendReply(
                            Methods.GetLocaleString(lang, "markdownBroken"), update);
                    }
                    else
                    {
                        Methods.SendError($"{e.Message}", update.Message, lang);
                    }
                }

            }
        }

        [Command(Trigger = "extralist", InGroupOnly = true)]
        public static void ExtraList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
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
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (args[1] != null)
            {
                var hash = $"chat:{update.Message.Chat.Id}:extra";
                var res = Redis.db.HashDeleteAsync(hash, args[1]);
                if (res.Result)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "extraDeleted"), update);
                    Service.LogCommand(update, update.Message.Text);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "noExtra"), update);
            }
        } 
      
        [Command(Trigger = "setlink", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetLink(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var link = "";
            if (update.Message.Chat.Username != null)
            {
                link = $"https://t.me/{update.Message.Chat.Username}";
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}links", "link", link);
                Bot.SendReply(Methods.GetLocaleString(lang, "linkSet"), update);
                Service.LogCommand(update, update.Message.Text);
            }
            else
            {
                if (args.Length == 2)
                {
                    link = args[1];
                    Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}links", "link", link);
                    Bot.SendReply(Methods.GetLocaleString(lang, "linkSet"), update);
                    Service.LogCommand(update, update.Message.Text);
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
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            long userId = 0;
            var chatId = update.Message.Chat.Id;
            if (args.Length == 2)
            {
                if (long.TryParse(args[1], out userId))
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
                    var user = Bot.Api.GetChatMemberAsync(chatId, Convert.ToInt32(userId)).Result;
                    var status = user.Status;
                    var name = user.User.FirstName;
                    if (user.User.Username != null)
                        name = $"{name} - @{user.User.Username}";
                    if (update.Message.Chat.Type == ChatType.Group && Methods.IsBanned(chatId, Convert.ToInt32(userId)))
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
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
                var user = Bot.Api.GetChatMemberAsync(chatId, Convert.ToInt32(userId)).Result;
                var status = user.Status;
                var name = user.User.FirstName;
                if (user.User.Username != null)
                    name = $"{name} - @{user.User.Username}";
                if (update.Message.Chat.Type == ChatType.Group && Methods.IsBanned(chatId, Convert.ToInt32(userId)))
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
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
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
                            Service.LogCommand(update, update.Message.Text);
                        }
                    }
                    if (args[1].Equals("a"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "a");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("r"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "r");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("m"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "m");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("ar") || args[1].Equals("ra"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "ra");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("mr") || args[1].Equals("rm"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "rm");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("am") || args[1].Equals("ma"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "am");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("ram") || args[1].Equals("rma") || args[1].Equals("arm") || args[1].Equals("amr") || args[1].Equals("mra") || args[1].Equals("mar"))
                    {
                       Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "ram");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("no"))
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "composed");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", "no");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "type", "custom");
                        Redis.db.HashSetAsync($"chat:{chatId}:welcome", "content", args[1]);
                        try
                        {
                            var res = Bot.SendReply(args[1], update);
                            var result =  Bot.Api.EditMessageTextAsync(chatId, res.MessageId, Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName));
                            Service.LogCommand(update, update.Message.Text);
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
                        Service.LogCommand(update, update.Message.Text);
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
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var userId = update.Message.From.Id;
            var chatId = update.Message.Chat.Id;
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            Redis.db.SetAddAsync($"chat:{chatId}:watch", userId);
            Bot.SendReply(Methods.GetLocaleString(lang, "off"), update);
            Service.LogCommand(update, update.Message.Text);
        }
        [Command(Trigger = "enablewatch", InGroupOnly = true, GroupAdminOnly = true)]
        public static void EnableMediaExcempt(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var userId = update.Message.From.Id;
            var chatId = update.Message.Chat.Id;
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            Redis.db.SetRemoveAsync($"chat:{chatId}:watch", userId);
            Bot.SendReply(Methods.GetLocaleString(lang, "on"), update);
            Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "check", InGroupOnly = true, GroupAdminOnly = true)]
        public static void CheckGroupUser(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                try
                {
                    var userid = Methods.GetUserId(update, args);
                    var status = Check(userid, update.Message.Chat.Id);
                    var n= Redis.db.HashGetAsync($"{update.Message.Chat.Id}:users:{userid}", "msgs").Result;
                    var number = n.HasValue ? int.Parse(n.ToString()) : 0;
                    Bot.SendReply(Methods.GetLocaleString(lang, $"usergroupstatus{status}", userid, number), update.Message);
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

        public static string Check(long userid, long chatid)
        {
            var status = Bot.Api.GetChatMemberAsync(chatid, Convert.ToInt32(userid)).Result.Status;
            var priv = Redis.db.SetContainsAsync($"chat:{chatid}:auth", userid).Result;
            var elevated = Redis.db.SetContainsAsync($"chat:{chatid}:mod", userid).Result;

            if (status == ChatMemberStatus.Creator) return "creator";
            if (priv) return "auth";
            if (status == ChatMemberStatus.Administrator) return "admin";
            if (status == ChatMemberStatus.Kicked) return "banned";
            if (elevated) return "elevated";
            if (status == ChatMemberStatus.Member) return "member";
            if (status == ChatMemberStatus.Left) return "left";
            return "neverseen";
        }

        [Command(Trigger = "addlogchannel", InGroupOnly = true)]
        public static void AddLogChat(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var role = Bot.Api.GetChatMemberAsync(chat, Convert.ToInt32((long)update.Message.From.Id));
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator || priv)
            {
                long channelId;
                
                var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
                if (long.TryParse(args[1], out channelId))
                {
                    if (channelId == chat)
                        return;
                    Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:settings", "logchat", channelId);
                    Redis.db.SetAddAsync("logChatGroups", update.Message.Chat.Id);
                    Service.LogCommand(update, update.Message.Text);

                    Bot.SendReply(Methods.GetLocaleString(lang, "channelAdded"), update);
                }
                else if (update.Message.ReplyToMessage != null)
                {
                    if (update.Message.ReplyToMessage.ForwardFromChat != null)
                    {
                        channelId = update.Message.ReplyToMessage.ForwardFromChat.Id;
                        if (channelId == chat)
                            return;
                        Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:settings", "logchat", channelId);
                        Redis.db.SetAddAsync("logChatGroups", update.Message.Chat.Id);
                        Service.LogCommand(update, update.Message.Text);

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
            var role = Bot.Api.GetChatMemberAsync(chat, Convert.ToInt32((long)update.Message.From.Id));
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator || priv)
            {
                Service.LogCommand(update, update.Message.Text);
                Redis.db.SetRemoveAsync("logChatGroups", update.Message.Chat.Id);
            }
        }

        [Command(Trigger = "temptime", InGroupOnly = true)]
        public static void SetDefaultTempban(Update update, string[] args)
        {
            int time;
            var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
            if (!int.TryParse(args[1], out time))
            {

                time = Methods.GetGroupTempbanTime(update.Message.Chat.Id);
                Bot.SendReply(Methods.GetLocaleString(lang, "defaultTime", time), update);
            }
            else
            {
                if (!Methods.IsGroupAdmin(update) &
                    !Methods.IsGlobalAdmin(update.Message.From.Id) & !Constants.Devs.Contains(update.Message.From.Id))
                {
                    Bot.SendReply(
                        Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message,true).Doc,
                            "userNotAdmin"), update.Message);
                    return;
                }
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:otherSettings", "tempbanTime", time);
                Bot.SendReply(Methods.GetLocaleString(lang, "defaultTimeSet"), update);
                Service.LogCommand(update, update.Message.Text);
            }
        }

        [Command(Trigger = "media", InGroupOnly = true, GroupAdminOnly = true)]
        public static void MediaUserMenu(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
            var warns = Convert.ToInt32(Redis.db.HashGetAsync($"chat:{chatId}:mediawarn", userid).Result);
            var text = Methods.GetLocaleString(lang, "getMediaWarn", warns);            
            var userMenu = new Menu(2);
            userMenu.Buttons = new List<InlineButton>
            {
                new InlineButton(Methods.GetLocaleString(lang, "removeWarn"), $"usermediaremwarns:{update.Message.Chat.Id}:{userid}"),
                new InlineButton(Methods.GetLocaleString(lang, "resetWarn"), $"usermediaresetwarn:{update.Message.Chat.Id}:{userid}")
                
            };
            Bot.SendReply(text, update, Key.CreateMarkupFromMenu(userMenu));
        }

    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "userbuttonresetwarn", GroupAdminOnly = true)]
        public static void UserButtonRemWarns(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = args[2];
             Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:warns", userId);            
             Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnsReset", call.From.FirstName));
        }

        [Callback(Trigger = "userbuttonremwarns", GroupAdminOnly = true)]
        public static void removeWarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = args[2];
            var res = Redis.db.HashDecrementAsync($"chat:{call.Message.Chat.Id}:warns", userId).Result;
            if (res < 0)
                Redis.db.HashSetAsync($"chat:{call.Message.Chat.Id}:warns", userId, 0);
            Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnRemoved", call.From.FirstName));
        }

        [Callback(Trigger = "userbuttonbanuser", GroupAdminOnly = true)]
        public static void UserButtonsBanUser(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = args[2];
            var isChatMember = Bot.Api.GetChatMemberAsync(call.Message.Chat.Id,int.Parse(userId)).Result;
            var res = Methods.BanUser(call.Message.Chat.Id, int.Parse(userId), lang);
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
            if (res)
            {
                if (isChatMember.Status == ChatMemberStatus.Member)
                {
                    Methods.SaveBan(int.Parse(userId), "ban");
                }
                if (int.Parse(userId) == 321720895 | int.Parse(userId) == 9375804)
                {
                    Redis.db.SetAdd("bot:lookaround",
                        $"{int.Parse(userId)}:\n{call.Message.Chat.Id} {call.Message.Chat.Title} {call.Message.From.Id} {call.Message.From.FirstName}");
                }
                Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                    Methods.GetLocaleString(lang, "userBanned"));
            }
             
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
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var num = Redis.db.HashIncrementAsync($"chat:{chatId}:warns", userId, 1).Result;
            var max = 3;
            int.TryParse(Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result, out max);
            
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

        [Callback(Trigger = "usermediaremwarns", GroupAdminOnly = true)]
        public static void removeMediaWarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = args[2];
            var res = Redis.db.HashDecrementAsync($"chat:{call.Message.Chat.Id}:mediawarn", userId).Result;
            if (res < 0)
                Redis.db.HashSetAsync($"chat:{call.Message.Chat.Id}:mediawarn", userId, 0);
            Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnRemoved", call.From.FirstName));
        }

        [Callback(Trigger = "usermediaresetwarn", GroupAdminOnly = true)]
        public static void RESETmediawarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = args[2];
            Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:mediawarn", userId);
            Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnRemoved", call.From.FirstName));
        }
    }
}
