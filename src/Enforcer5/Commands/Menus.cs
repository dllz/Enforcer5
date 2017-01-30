using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5   
{
    public static partial class Commands
    {
        [Command(Trigger = "menu", GroupAdminOnly = true, InGroupOnly = true)]
        public static async Task Menu(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            var settings = Redis.db.HashGetAllAsync($"chat:{chatId}:settings").Result;
            var mainMenu = new Menu();
            mainMenu.Columns = 2;
            var menuText = Methods.GetLocaleString(lang, "mainMenu", update.Message.Chat.Title);
            mainMenu.Buttons = new List<InlineButton>();
            foreach (var mem in settings)
            {
                if (mem.Name.Equals("Flood")  || mem.Name.Equals("Report") || mem.Name.Equals("Welcome"))
                {
                    mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"), $"menu:settings:{mem.Name}"));
                    if (mem.Value.Equals("yes"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("🚫", $"menu:{mem.Name}:{chatId}"));
                    }
                    else if (mem.Value.Equals("no"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("✅", $"menu:{mem.Name}:{chatId}"));
                    }
                }
                else if (mem.Name.Equals("Modlist") || mem.Name.Equals("About") || mem.Name.Equals("Rules") ||
                         mem.Name.Equals("Extra"))
                {
                    mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"), $"menu:settings:{mem.Name}"));
                    if (mem.Value.Equals("yes"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("👤", $"menu:{mem.Name}:{chatId}"));
                    }
                    else if (mem.Value.Equals("no"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("👥", $"menu:{mem.Name}:{chatId}"));
                    }
                }                
            }
            settings = Redis.db.HashGetAllAsync($"chat:{chatId}:char").Result;
            foreach (var mem in settings)
            {
                mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"), $"menu:settings:{mem.Name}"));
                if (mem.Value.Equals("kick") || mem.Value.Equals("ban"))
                {
                    mainMenu.Buttons.Add(new InlineButton("🔐", $"menu:{mem.Name}:{chatId}"));
                }
                else if (mem.Value.Equals("allowed"))
                {
                    mainMenu.Buttons.Add(new InlineButton("✅", $"menu:{mem.Name}:{chatId}"));
                }
            }
            var max = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result;
            var action = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").Result;
            var warnTitle = new Menu(1);
            warnTitle.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "WarnsButton"), "menu:alert:warns"));
            var editWarn = new Menu(3);
            editWarn.Buttons.Add(new InlineButton("➖", $"menu:DimWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton($"📍 {max} 🔨 {action}", $"menu:ActionWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton("➕", $"menu:RaiseWarn:{chatId}"));
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "closeButton"), "close"));            
            var menu = Key.CreateMarkupFromMenus(mainMenu, warnTitle, editWarn, close);
            await Bot.Send(menuText, update.Message.From.Id, customMenu:menu);
            await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
        }
        [Command(Trigger = "dashboard", GroupAdminOnly = true, InGroupOnly = true)]
        public static async Task Dashboard(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            //var buttons = new[]
            //{
            //    new InlineKeyboardButton(), 
            //}
        }
    }
}
