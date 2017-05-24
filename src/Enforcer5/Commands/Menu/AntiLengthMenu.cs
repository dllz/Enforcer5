using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class Commands
    {
        public static InlineKeyboardMarkup genAntiLengthMenu(long chatId, XDocument lang)
        {
            var nameSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:antinamelengthsettings").Result;
            var textSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:antitextlengthsettings").Result;

            var menu = new Menu(2);
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "nameSettingsHeader")));
            menu.Buttons.Add(new InlineButton($"{nameSettings.Where(e => e.Name.Equals("maxlength")).FirstOrDefault().Value}"));
            foreach (var mem in nameSettings)
            {
                if (mem.Value.Equals("yes"))
                {
                    menu.Buttons.Add(new InlineButton($"✅ | {Methods.GetLocaleString(lang, "on")}",
                        $"namesettings:{chatId}"));
                }
                else if (mem.Value.Equals("no"))
                {
                    menu.Buttons.Add(new InlineButton($"❌ | {Methods.GetLocaleString(lang, "off")}",
                        $"namesettings:{chatId}"));
                }  switch (mem.Value.ToString())
                    {
                        case "kick":
                            menu.Buttons.Add(new InlineButton($"⚡️ | {Methods.GetLocaleString(lang, "kick")}", $"namesettingsaction:{chatId}"));
                            break;
                        case "ban":
                            menu.Buttons.Add(new InlineButton($"⛔ | {Methods.GetLocaleString(lang, "ban")}", $"namesettingsaction:{chatId}"));
                            break;
                        case "Warn":
                            menu.Buttons.Add(new InlineButton($"⚠️ | {Methods.GetLocaleString(lang, "Warn")}", $"namesettingsaction:{chatId}"));
                            break;
                        case "tempban":
                            menu.Buttons.Add(new InlineButton($"⏳ | {Methods.GetLocaleString(lang, "tempban")}", $"namesettingsaction:{chatId}"));
                            break;

                    }
                
            }
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "textSettingsHeader")));
            menu.Buttons.Add(new InlineButton($"{textSettings.Where(e => e.Name.Equals("maxlength")).FirstOrDefault().Value} : {textSettings.Where(e => e.Name.Equals("maxlines")).FirstOrDefault().Value}"));
            foreach (var mem in textSettings)
            {
                if (mem.Value.Equals("yes"))
                {
                    menu.Buttons.Add(new InlineButton($"✅ | {Methods.GetLocaleString(lang, "on")}",
                        $"textsettings:{chatId}"));
                }
                else if (mem.Value.Equals("no"))
                {
                    menu.Buttons.Add(new InlineButton($"❌ | {Methods.GetLocaleString(lang, "off")}",
                        $"textsettings:{chatId}"));
                }
                else
                {
                    switch (mem.Value.ToString())
                    {
                        case "kick":
                            menu.Buttons.Add(new InlineButton($"⚡️ | {Methods.GetLocaleString(lang, "kick")}", $"textsettingsaction:{chatId}"));
                            break;
                        case "ban":
                            menu.Buttons.Add(new InlineButton($"⛔ | {Methods.GetLocaleString(lang, "ban")}", $"textsettingsaction:{chatId}"));
                            break;
                        case "Warn":
                            menu.Buttons.Add(new InlineButton($"⚠️ | {Methods.GetLocaleString(lang, "Warn")}", $"textsettingsaction:{chatId}"));
                            break;
                        case "tempban":
                            menu.Buttons.Add(new InlineButton($"⏳ | {Methods.GetLocaleString(lang, "tempban")}", $"textsettingsaction:{chatId}"));
                            break;

                    }
                }
            }
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, close);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openLengthMenu", GroupAdminOnly = true)]
        public static void openLengthMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId);
            var text = Methods.GetLocaleString(lang.Doc, "lengthMenu", lang.Base);
            var keys = Commands.genAntiLengthMenu(chatId, lang.Doc);
            Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode: ParseMode.Html);
        }

        [Callback(Trigger = "namesettingsaction")]
        public static void NameSettingsAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "action";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:antinamelengthsettings", option).Result;
            if (current.Equals("ban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "kick");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "Warn");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("Warn"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "tempban");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("tempban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "ban");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "namesettings")]
        public static void NameSettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "enabled";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:antinamelengthsettings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "no");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "yes");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "textsettingsaction")]
        public static void TextSettingsAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "action";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:antitextlengthsettings", option).Result;
            if (current.Equals("ban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "kick");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "Warn");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("Warn"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "tempban");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            } else if (current.Equals("tempban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "ban");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "textsettings")]
        public static void TextSettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "enabled";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:antitextlengthsettings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "no");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "yes");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
    }
}
