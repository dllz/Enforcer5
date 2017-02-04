using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Handlers;
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
        [Command(Trigger = "uploadlanguage", UploadAdmin = true)]
        public static async Task UploadLang(Update update, string[] args)
        {
            try
            {
                var id = update.Message.Chat.Id;
                if (update.Message.ReplyToMessage?.Type != MessageType.DocumentMessage)
                {
                    await Bot.Send("Please reply to the file with /uploadlanguage", id);
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
                await Bot.Api.SendTextMessageAsync(update.Message.Chat.Id, e.Message, parseMode: ParseMode.Default);
            }
        }

        [Command(Trigger = "validatelanguages", UploadAdmin = true)]
        public static async Task ValidateLangs(Update update, string[] args)
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
                await Bot.Api.SendTextMessageAsync(update.Message.Chat.Id, "Validate which language?",
                    replyToMessageId: update.Message.MessageId, replyMarkup: menu);
            }
            catch (AggregateException e)
            {
                foreach (var ex in e.InnerExceptions)
                {
                    var x = ex as ApiRequestException;

                    await Bot.Send(x.Message, update.Message.Chat.Id);
                }
            }
            catch (ApiRequestException ex)
            {
                await Bot.Send(ex.Message, update.Message.Chat.Id);
            }
        }

        [Command(Trigger = "getlanguage", UploadAdmin = true)]
        public static async Task GetLang(Update update, string[] args)
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
               await Bot.SendReply(Methods.GetLocaleString(langs.FirstOrDefault(e => e.Name.Equals("English")).Doc, "GetLang"), update, keyboard:menu);
            }
            catch (AggregateException e)
            {
                foreach (var ex in e.InnerExceptions)
                {
                    var x = ex as ApiRequestException;

                    await Bot.Send(x.Message, update.Message.Chat.Id);
                }
            }
            catch (ApiRequestException ex)
            {
                await Bot.Send(ex.Message, update.Message.Chat.Id);
            }
        }

        [Command(Trigger = "halt", GlobalAdminOnly = true)]
        public static async Task StopBot(Update update, string[] args)
        {
            await Bot.SendReply("Stopping bot", update);
            if (update.Message.From.Id != Constants.Devs[0])
            {
                await Bot.Send($"The bot has been stopped by {update.Message.From.Id} {update.Message.From.FirstName}",
                    Constants.Devs[0]);
            }
            Redis.SaveRedis();
            Environment.Exit(0);
        }

        [Command(Trigger = "makelangadmin", RequiresReply = true, UploadAdmin = true)]
       public static async Task MakeLangAdmin(Update update, string[] args)
        {
            var id = update.Message.ReplyToMessage.From.Id;
            var res =  Redis.db.SetAddAsync("langAdmins", id).Result;
            await Bot.SendReply("Done", update);
        }

        [Command(Trigger = "removelangadmin", RequiresReply = true, UploadAdmin = true)]
        public static async Task RemoveLangAdmin(Update update, string[] args)
        {
            var id = update.Message.ReplyToMessage.From.Id;
            var res =  Redis.db.SetRemoveAsync("langAdmins", id).Result;
            await Bot.SendReply("Done", update);
        }
        [Command(Trigger = "reloadLang", UploadAdmin = true)]
        public static async Task reloadLang(Update update, string[] args)
        {
            Methods.IntialiseLanguages();
            await Bot.SendReply("Done", update);
        }

    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "validate", UploadAdmin = true)]
        public static Task validateLang(CallbackQuery query, string[] args)
        {
            var command = args[0];
            var choice = "";
            if (args.Length > 2)
                choice = args[2];
            //choice = args[1];
            if (choice == "All")
            {
                LanguageHelper.ValidateFiles(query.Message.Chat.Id, query.Message.MessageId);
                return null;
            }

            if (args[4] != "base" && args[3] == "All")
            {
                LanguageHelper.ValidateFiles(query.Message.Chat.Id, query.Message.MessageId, choice);
                return null;
            }

            var vlang = Program.LangaugeList.Where(e => e.Name.Equals(choice)).FirstOrDefault();

            //var menu = new ReplyKeyboardHide { HideKeyboard = true, Selective = true };
            //Bot.SendTextMessage(id, "", replyToMessageId: update.Message.MessageId, replyMarkup: menu);
            LanguageHelper.ValidateLanguageFile(query.Message.Chat.Id, vlang.FilePath, query.Message.MessageId);
            return null;
        }

        [Callback(Trigger = "getlang", UploadAdmin = true)]
        public static Task getLang(CallbackQuery query, string[] args)
        {
            var command = args[0];
            var choice = "";
            if (args.Length > 2)
                choice = args[2];
            if (choice == "All")
            {
                Bot.ReplyToCallback(query, "One moment...");
                LanguageHelper.SendAllFiles(query.Message.Chat.Id);
                return null;
            }

            if (args[4] != "base" && args[3] == "All")
            {
                Bot.ReplyToCallback(query, "One moment...");
                LanguageHelper.SendBase(choice, query.Message.Chat.Id);
                return null;
            }

            var glang = Program.LangaugeList.Where(e => e.Name.Equals(choice)).FirstOrDefault();
            Bot.ReplyToCallback(query, "One moment...");
            LanguageHelper.SendFile(query.Message.Chat.Id, glang.Name);
            return null;
        }

        [Callback(Trigger = "upload", UploadAdmin = true)]
        public static Task uploadLang(CallbackQuery query, string[] args)
        {
            var command = args[0];
            var choice = "";
            if (args.Length > 2)
                choice = args[2];
            if (choice == "current")
            {
                Bot.ReplyToCallback(query, "No action taken.");
                return null;
            }
            LanguageHelper.UseNewLanguageFile(choice, query.Message.Chat.Id, query.Message.MessageId);
            return null;
        }       
    }
}
