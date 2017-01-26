using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5   
{
    public static partial class Commands
    {
        [Command(Trigger = "menu", GroupAdminOnly = true, InGroupOnly = true)]
        public static async void Menu(Update update, string[] args)
        {
            //var bu
        }
        [Command(Trigger = "dashboard", GroupAdminOnly = true, InGroupOnly = true)]
        public static async void Dashboard(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            //var buttons = new[]
            //{
            //    new InlineKeyboardButton(), 
            //}
        }
    }
}
