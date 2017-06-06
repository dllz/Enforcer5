using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Handlers;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
#pragma warning disable 4014
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "uploadlanguage", UploadAdmin = true)]
        public static void UploadLang(Update update, string[] args)
        {
            try
            {
                var id = update.Message.Chat.Id;
                if (update.Message.ReplyToMessage?.Type != MessageType.DocumentMessage)
                {
                     Bot.Send("Please reply to the file with /uploadlanguage", id);
                    return;
                }
                var fileid = update.Message.ReplyToMessage.Document?.FileId;
                if (fileid != null)
                {
                    try
                    {
                          LanguageHelper.UploadFile(fileid, id,
                            update.Message.ReplyToMessage.Document.FileName,
                            update.Message.MessageId);
                    }
                    catch (Exception e)
                    {
                        Bot.SendReply(e.ToString(), update);
                    }
                }
            }
            catch (Exception e)
            {
                Bot.Api.SendTextMessageAsync(update.Message.Chat.Id, e.Message, parseMode: ParseMode.Default);
            }
        }

        [Command(Trigger = "validatelanguages", UploadAdmin = true)]
        public static void ValidateLangs(Update update, string[] args)
        {
            var langs = Program.LangaugeList;


            List<InlineKeyboardButton> buttons =
                langs.Select(x => x.Base)
                    .Distinct()
                    .OrderBy(x => x)
                    .Select(x => new InlineKeyboardButton(x, $"validate:{update.Message.From.Id}:{x}:null:base"))
                    .ToList();
            //buttons.Insert(0, new InlineKeyboardButton("All", $"validate|{update.Message.From.Id}|All|null|base"));

            var baseMenu = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < buttons.Count; i++)
            {
                if (buttons.Count - 1 == i)
                {
                    baseMenu.Add(new[] {buttons[i]});
                }
                else
                    baseMenu.Add(new[] {buttons[i], buttons[i + 1]});
                i++;
            }

            var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
            try
            {
                 Bot.Api.SendTextMessageAsync(update.Message.Chat.Id, "Validate which language?",
                    replyToMessageId: update.Message.MessageId, replyMarkup: menu);
            }
            catch (AggregateException e)
            {
                foreach (var ex in e.InnerExceptions)
                {
                    var x = ex as ApiRequestException;

                     Bot.Send(x.Message, update.Message.Chat.Id);
                }
            }
            catch (ApiRequestException ex)
            {
                 Bot.Send(ex.Message, update.Message.Chat.Id);
            }
        }

        [Command(Trigger = "getlanguage", UploadAdmin = true)]
        public static void GetLang(Update update, string[] args)
        {
            var langs = Program.LangaugeList;


            List<InlineKeyboardButton> buttons = langs.Select(x => x.Base).Distinct().OrderBy(x => x).Select(x => new InlineKeyboardButton(x, $"getlang:{update.Message.From.Id}:{x}:null:base")).ToList();
            buttons.Insert(0, new InlineKeyboardButton("All", $"getlang:{update.Message.From.Id}:All:null:base"));

            var baseMenu = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < buttons.Count; i++)
            {
                if (buttons.Count - 1 == i)
                {
                    baseMenu.Add(new[] { buttons[i] });
                }
                else
                    baseMenu.Add(new[] { buttons[i], buttons[i + 1] });
                i++;
            }

            var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
            try
            {
                Bot.SendReply(Methods.GetLocaleString(langs.FirstOrDefault(e => e.Name.Equals("English")).Doc, "GetLang"), update, keyboard:menu);
            }
            catch (AggregateException e)
            {
                foreach (var ex in e.InnerExceptions)
                {
                    var x = ex as ApiRequestException;

                     Bot.Send(x.Message, update.Message.Chat.Id);
                }
            }
            catch (ApiRequestException ex)
            {
                 Bot.Send(ex.Message, update.Message.Chat.Id);
            }
        }

        [Command(Trigger = "halt", DevOnly = true)]
        public static void StopBot(Update update, string[] args)
        {
             Bot.SendReply("Stopping bot", update);
            if (update.Message.From.Id != Constants.Devs[0])
            {
                 Bot.Send($"The bot has been stopped by {update.Message.From.Id} {update.Message.From.FirstName}",
                    Constants.Devs[0]);
            }
            try
            {
                Redis.SaveRedis();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            Environment.Exit(0);
        }

        [Command(Trigger = "makelangadmin", RequiresReply = true, UploadAdmin = true)]
       public static void MakeLangAdmin(Update update, string[] args)
        {
            var id = update.Message.ReplyToMessage.From.Id;
            var res =  Redis.db.SetAddAsync("langAdmins", id).Result;
             Bot.SendReply("Done", update);
        }

        [Command(Trigger = "removelangadmin", RequiresReply = true, UploadAdmin = true)]
        public static void RemoveLangAdmin(Update update, string[] args)
        {
            var id = update.Message.ReplyToMessage.From.Id;
            var res =  Redis.db.SetRemoveAsync("langAdmins", id).Result;
             Bot.SendReply("Done", update);
        }
        [Command(Trigger = "reloadLang", UploadAdmin = true)]
        public static void reloadLang(Update update, string[] args)
        {
            Program.LangaugeList = null;
            Program.LangaugeList = new List<Language>();
            foreach (var language in Directory.GetFiles(Bot.LanguageDirectory, "*.xml"))
            {

                Program.LangaugeList.Add(new Language(language));
            }
             Bot.SendReply("Done", update);
        }

        [Command(Trigger = "getrekt", DevOnly = true)]
        public static void GlobalBan(Update update, string[] args)
        {
            int userId = 0;
            string moti = "";
            if (update.Message.ReplyToMessage != null)
            {
                if (update.Message.ReplyToMessage.ForwardFrom != null)
                {
                    userId = update.Message.ReplyToMessage.ForwardFrom.Id;
                }
                else
                {
                    userId = update.Message.ReplyToMessage.From.Id;
                }
                moti = args[1];
            }            
            if (args.Length == 2)
            {
                int temp;
                var spilt = args[1].Split(':');
                if (int.TryParse(spilt[0], out temp))
                {
                    userId = temp;
                    moti = spilt[1];
                }
                else
                {
                    moti = args[1];
                }                
            }
            else
            {
                return;
            }
            if (userId != 0)
            {
                 Redis.db.HashSetAsync($"globalBan:{userId}", "banned", 1);
                 Redis.db.HashSetAsync($"globalBan:{userId}", "motivation", moti);
                 Redis.db.HashSetAsync($"globalBan:{userId}", "time", System.DateTime.UtcNow.ToString());
                 Bot.SendReply($"{userId} has been rekt for {moti}", update);
            }
            else
            {
                 Bot.SendReply("Nopes", update);
            }
        }

        [Command(Trigger = "allowp", GlobalAdminOnly = true, InGroupOnly = true)]
        public static void AllowPremiumBot(Update update, string[] args)
        {
             Redis.db.SetAddAsync("premiumBot", update.Message.Chat.Id);
             Bot.SendReply("Activated", update);
        }

        [Command(Trigger = "blockp", GlobalAdminOnly = true, InGroupOnly = true)]
        public static void BlockPremiumBot(Update update, string[] args)
        {
             Redis.db.SetRemoveAsync("premiumBot", update.Message.Chat.Id);
             Bot.SendReply("Deactivated", update);
        }

        [Command(Trigger = "unrekt", GlobalAdminOnly = true)]
        public static void Unrekt(Update update, string[] args)
        {
            int userId = 0;
            string moti = "";
            if (update.Message.ReplyToMessage != null)
            {
                if (update.Message.ReplyToMessage.ForwardFrom != null)
                {
                    userId = update.Message.ReplyToMessage.ForwardFrom.Id;
                }
                else
                {
                    userId = update.Message.ReplyToMessage.From.Id;
                }
                moti = args[1];
            }
            if (args.Length == 2)
            {
                int temp;
                var spilt = args[1].Split(':');
                if (int.TryParse(spilt[0], out temp))
                {
                    userId = temp;
                    moti = spilt[1];
                }
                else
                {
                    moti = args[1];
                }
            }
            else
            {
                return;
            }
            if (userId != 0)
            {
                 Redis.db.HashSetAsync($"globalBan:{userId}", "banned", 0);
                 Redis.db.HashSetAsync($"globalBan:{userId}", "unbanMotivation", moti);
                 Redis.db.HashSetAsync($"globalBan:{userId}", "unbanTime", System.DateTime.UtcNow.ToString());
                 Bot.SendReply($"{userId} has been unrekt for {moti}", update);
            }
            else
            {
                 Bot.SendReply("Nopes", update);
            }
        }

        [Command(Trigger = "leave", GlobalAdminOnly = true)]
        public static void LeaveChat(Update update, string[] args)
        {
            try
            {
                var chatId = args[1];
                 Bot.Api.LeaveChatAsync(chatId);
                 Bot.SendReply("The chat has been left", update);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);                
            }
        }

        [Command(Trigger = "rektlist", GlobalAdminOnly = true)]
        public static void GlobalBanList(Update update, string[] args)
        {
            var bans = Redis.db.HashScan("globalBan", default(RedisValue), Int32.MaxValue, 1L);
            var ids = bans.Select(id => id.Name).ToList();
            var basicID = new List<int>();
            foreach (var id in ids)
            {
                basicID.Add(int.Parse(id.ToString().Split(':')[1]));
            }
            var banInfo = new List<string>();
            foreach (var user in basicID)
            {
                var entry = Redis.db.HashGetAllAsync($"globalBan:{user}").Result;
                if (entry.Where(i => i.Name.Equals("banned")).Select(e => e.Name).Equals(1))
                {
                    var motivation = entry.Where(e => e.Name.Equals("motivation")).Select(i => i.Value);
                    var time = entry.Where(i => i.Name.Equals("time")).Select(e => e.Value);
                    banInfo.Add($"User: {user} for {motivation} at {time}");
                }
            }
             Bot.SendReply(String.Join("\n", banInfo), update);
        }

        [Command(Trigger = "mediaid", DevOnly = true, RequiresReply = true)]
        public static void GetMediaId(Update update, string[] args)
        {
            var mediaID = Methods.GetMediaId(update.Message);
           
             Bot.SendReply($"Media: {mediaID}", update);
        }

        [Command(Trigger = "msgid", DevOnly = true, RequiresReply = true)]
        public static void GetMessageID(Update update, string[] args)
        {
           
            Bot.SendReply($"Message: {update.Message.ReplyToMessage.MessageId}", update);
        }


        [Command(Trigger = "getuser", DevOnly = true)]
        public static void GetUserDetails(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var userid = Methods.GetUserId(update, args);
            var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
            var isBanned = Redis.db.HashGetAllAsync($"globalBan:{userid}").Result;
            foreach (var mem in isBanned)
            {
                text = $"{text}\n{mem.Name}: {mem.Value}";
            }
            var msgs = Redis.db.HashGetAsync($"chat:{userid}", "msgs").Result;
            text = $"{text}\nUser has said {msgs} ever";
             Bot.SendReply(text, update);
        }

        [Command(Trigger = "look", GlobalAdminOnly = true)]
        public static void Look(Update update, string[] args)
        {
            var data = Redis.db.SetMembersAsync("bot:lookaround").Result;
            StringBuilder res = new StringBuilder();
            foreach (var mem in data)
            {
                res.Append("\n");
                res.Append(mem.ToString());
            }
            if(!string.IsNullOrEmpty(res.ToString()))
                 Bot.SendReply(res.ToString(), update);
            else
            {
                 Bot.SendReply("Nothing to see", update);
            }
        }

        [Command(Trigger = "unlook", GlobalAdminOnly = true)]
        public static void unLook(Update update, string[] args)
        {
            var data = Redis.db.SetMembersAsync("bot:lookaround").Result;
            foreach (var mem in data)
            {
                 Redis.db.SetRemoveAsync("bot:lookaround", mem.ToString());
            }
             Bot.SendReply("done", update);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "validate", UploadAdmin = true)]
        public static void validateLang(CallbackQuery query, string[] args)
        {
            var command = args[0];
            var choice = "";
            if (args.Length > 2)
                choice = args[2];
            //choice = args[1];
            if (choice == "All")
            {
                LanguageHelper.ValidateFiles(query.Message.Chat.Id, query.Message.MessageId);
            }

            if (args[4] != "base" && args[3] == "All")
            {
                LanguageHelper.ValidateFiles(query.Message.Chat.Id, query.Message.MessageId, choice);
            }

            var vlang = Program.LangaugeList.Where(e => e.Name.Equals(choice)).FirstOrDefault();

            //var menu = new ReplyKeyboardHide { HideKeyboard = true, Selective = true };
            //Bot.SendTextMessage(id, "", replyToMessageId: update.Message.MessageId, replyMarkup: menu);
            LanguageHelper.ValidateLanguageFile(query.Message.Chat.Id, vlang.FilePath, query.Message.MessageId);
        }

        [Callback(Trigger = "getlang", UploadAdmin = true)]
        public static void getLang(CallbackQuery query, string[] args)
        {
            var command = args[0];
            var choice = "";
            if (args.Length > 2)
                choice = args[2];
            if (choice == "All")
            {
                Bot.ReplyToCallback(query, "One moment...");
                LanguageHelper.SendAllFiles(query.Message.Chat.Id);
            }

            if (args[4] != "base" && args[3] == "All")
            {
                Bot.ReplyToCallback(query, "One moment...");
                LanguageHelper.SendBase(choice, query.Message.Chat.Id);
            }

            var glang = Program.LangaugeList.FirstOrDefault(e => e.Name.Equals(choice));
            Bot.ReplyToCallback(query, "One moment...");
            LanguageHelper.SendFile(query.Message.Chat.Id, glang.Name);
        }

        [Callback(Trigger = "upload", UploadAdmin = true)]
        public static void uploadLang(CallbackQuery query, string[] args)
        {
            var command = args[0];
            var choice = "";
            if (args.Length > 2)
                choice = args[2];
            if (choice == "current")
            {
                Bot.ReplyToCallback(query, "No action taken.");
            }
            LanguageHelper.UseNewLanguageFile(choice, query.Message.Chat.Id, query.Message.MessageId);
        }       
    }
}
