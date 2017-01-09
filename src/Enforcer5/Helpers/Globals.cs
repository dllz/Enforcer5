using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Handlers;
using Enforcer5.Models;
using Microsoft.Win32;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Helpers
{
    internal static class Bot
    {
        internal static string TelegramAPIKey;
        public static TelegramBotClient Api;
        public static User Me;
        public static DateTime StartTime = DateTime.UtcNow;
        public static bool Running = true;
        public static long CommandsReceived = 0;
        public static long MessagesProcessed = 0;
        public static long MessagesReceived = 0;
        public static long TotalPlayers = 0;
        public static long TotalGames = 0;
        public static Random R = new Random();
        public static XDocument English;
        public static int MessagesSent = 0;
        public static string CurrentStatus = "";
        internal static string RootDirectory
        {
            get
            {
                var path = AppContext.BaseDirectory;;
                return Path.GetDirectoryName(path);
            }
        }
        internal delegate void ChatCommandMethod(Update u, string[] args);
        internal static List<Command> Commands = new List<Command>();
        internal static string LanguageDirectory => Path.GetFullPath(Path.Combine(RootDirectory, @"..\..\Languages"));
        internal static string TempLanguageDirectory => Path.GetFullPath(Path.Combine(RootDirectory, @"..\..\TempLanguageFiles"));
        public static async void Initialize(string updateid = null)
        {

            //get api token from registry
            var key =
                    RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey("SOFTWARE\\Werewolf");
            TelegramAPIKey = key.GetValue("EnforcerAPI").ToString();
            Api = new TelegramBotClient(TelegramAPIKey);
            await Send($"Bot Started:\n{System.DateTime.Now.Date}", Constants.Devs[0]);

            //load the commands list
            //foreach (var m in typeof(Commands).GetMethods())
            //{
            //    var c = new Command();
            //    foreach (var a in m.GetCustomAttributes(true))
            //    {
            //        if (a is Attributes.Command)
            //        {
            //            var ca = a as Attributes.Command;
            //            c.Blockable = ca.Blockable;
            //            c.DevOnly = ca.DevOnly;
            //            c.GlobalAdminOnly = ca.GlobalAdminOnly;
            //            c.GroupAdminOnly = ca.GroupAdminOnly;
            //            c.Trigger = ca.Trigger;
            //            c.Method = (ChatCommandMethod)Delegate.CreateDelegate(typeof(ChatCommandMethod), m);
            //            c.InGroupOnly = ca.InGroupOnly;
            //            Commands.Add(c);
            //        }
            //    }
            //}

            Api.OnInlineQuery += UpdateHandler.InlineQueryReceived;
            Api.OnUpdate += UpdateHandler.UpdateReceived;
            Api.OnCallbackQuery += UpdateHandler.CallbackReceived;
            Api.OnReceiveError += ApiOnReceiveError;
            Me = Api.GetMeAsync().Result;

            Console.Title += " " + Me.Username;
            StartTime = DateTime.UtcNow;
            //now we can start receiving
            Api.StartReceiving();
        }

        internal static void ReplyToCallback(CallbackQuery query, string text = null, bool edit = true, bool showAlert = false, InlineKeyboardMarkup replyMarkup = null)
        {
            //first answer the callback
            Bot.Api.AnswerCallbackQueryAsync(query.Id, edit ? null : text, showAlert);
            //edit the original message
            if (edit)
                Edit(query, text, replyMarkup);
        }

        internal static Task<Message> Edit(CallbackQuery query, string text, InlineKeyboardMarkup replyMarkup = null)
        {
            return Edit(query.Message.Chat.Id, query.Message.MessageId, text, replyMarkup);
        }

        internal static Task<Message> Edit(long id, int msgId, string text, InlineKeyboardMarkup replyMarkup = null)
        {
            Bot.MessagesSent++;
            return Bot.Api.EditMessageTextAsync(id, msgId, text, replyMarkup: replyMarkup);
        }


        private static void ApiOnReceiveError(object sender, ReceiveErrorEventArgs receiveErrorEventArgs)
        {
            if (!Api.IsReceiving)
            {
                Api.StartReceiving();
            }
            var e = receiveErrorEventArgs.ApiRequestException;
            using (var sw = System.IO.File.AppendText(Path.Combine(RootDirectory, "..\\Logs\\apireceiveerror.log")))
            {
                sw.WriteLine($"{DateTime.Now} {e.ErrorCode} - {e.Message}\n{e.Source}");
            }
                
        }

        internal static async Task<Message> Send(string message, long id, bool clearKeyboard = false,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Markdown)
        {
            MessagesSent++;
            //message = message.Replace("`",@"\`");
            if (clearKeyboard)
            {
                var menu = new ReplyKeyboardHide {HideKeyboard = true};
                return await Api.SendTextMessageAsync(id, message, replyMarkup: menu, disableWebPagePreview: true,
                    parseMode: parseMode);
            }
            else if (customMenu != null)
            {
                return await Api.SendTextMessageAsync(id, message, replyMarkup: customMenu, disableWebPagePreview: true,
                    parseMode: parseMode);
            }
            else
            {
                return await Api.SendTextMessageAsync(id, message, disableWebPagePreview: true, parseMode: parseMode);
            }

        }

        internal static async Task<Message> SendReply(string message, Message msg, IReplyMarkup replyMarkup = null)
        {
            return await Api.SendTextMessageAsync(msg.Chat.Id, message, false, false, msg.MessageId, replyMarkup);
        }
    }

    internal static class Redis
    {
        static ConnectionMultiplexer redis = ConnectionMultiplexer.Connect("localhost");
        public static IDatabase db = redis.GetDatabase(Constants.EnforcerDB);
    }
}
