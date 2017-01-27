using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Handlers;
using Enforcer5.Models;
using Microsoft.Win32;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
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

        internal delegate void ChatCallbackMethod(CallbackQuery u, string[] args);
        internal static List<Models.Commands> Commands = new List<Models.Commands>();
        internal static List<Models.CallBacks> CallBacks = new List<Models.CallBacks>();
        internal static string LanguageDirectory => Path.GetFullPath(Path.Combine(RootDirectory, @"..\..\..\Languages"));
        internal static string TempLanguageDirectory => Path.GetFullPath(Path.Combine(RootDirectory, @"..\..\TempLanguageFiles"));
        public static async void Initialize(string updateid = null)
        {
            try
            {
                //get api token from registry
                var key =
                        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                            .OpenSubKey("SOFTWARE\\Werewolf");
                TelegramAPIKey = key.GetValue("EnforcerAPI").ToString();
                Api = new TelegramBotClient(TelegramAPIKey);
                await Send($"Bot Started:\n{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}", Constants.Devs[0]);

                //load the commands list
                foreach (var m in typeof(Commands).GetMethods())
                {
                    var c = new Models.Commands();
                    foreach (var a in m.GetCustomAttributes(true))
                    {
                        if (a is Attributes.Command)
                        {
                            var ca = a as Attributes.Command;
                            c.Blockable = ca.Blockable;
                            c.DevOnly = ca.DevOnly;
                            c.GlobalAdminOnly = ca.GlobalAdminOnly;
                            c.GroupAdminOnly = ca.GroupAdminOnly;
                            c.Trigger = ca.Trigger;
                            c.Method = (ChatCommandMethod)m.CreateDelegate(typeof(ChatCommandMethod));
                            c.InGroupOnly = ca.InGroupOnly;
                            c.RequiresReply = ca.RequiresReply;
                            Commands.Add(c);
                        }
                    }
                }
                //loadCallbackQuries
                foreach (var m in typeof(CallBacks).GetMethods())
                {
                    var c = new Models.CallBacks();
                    foreach (var a in m.GetCustomAttributes(true))
                    {
                        if (a is Attributes.Callback)
                        {
                            var ca = a as Attributes.Callback;
                            c.Blockable = ca.Blockable;
                            c.DevOnly = ca.DevOnly;
                            c.GlobalAdminOnly = ca.GlobalAdminOnly;
                            c.GroupAdminOnly = ca.GroupAdminOnly;
                            c.Trigger = ca.Trigger;
                            c.Method = (ChatCallbackMethod)m.CreateDelegate(typeof(ChatCallbackMethod));
                            c.InGroupOnly = ca.InGroupOnly;
                            c.RequiresReply = ca.RequiresReply;
                            CallBacks.Add(c);
                        }
                    }
                }

                Api.OnInlineQuery += UpdateHandler.InlineQueryReceived;
                Api.OnUpdate += UpdateHandler.UpdateReceived;
                Api.OnCallbackQuery += UpdateHandler.CallbackHandler;

                Me = Api.GetMeAsync().Result;

                Console.Title += " " + Me.Username;
                StartTime = DateTime.UtcNow;
                //now we can start receiving
                Api.StartReceiving();
            }
            catch (ApiRequestException e)
            {
                try
                {
                        await Bot.Send($"@falconza shit happened right at the top \n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}", -1001094155678);
                }
                catch (ApiRequestException ex)
                {
                    //fuckit   
                }
                catch (Exception exception)
                {
                    //fuckit
                }
            }
            catch (AggregateException e)
            {
                await Bot.Send($"#TopLevel {e.InnerExceptions[0]}\n{e.StackTrace}", -1001094155678);
            }
            catch (Exception ex)
            {                
                try
                {
                    await Bot.Send($"@falconza shit happened right at the top \n{ex.Message}\n\n{ex.StackTrace}", -1001094155678);
                }
                catch (Exception e)
                {
                    try
                    {
                        await Bot.Send($"@falconza shit happened\n{ex.Message}\n\n{ex.StackTrace}", 125311351);
                    }
                    catch (Exception exception)
                    {
                        //fuckit
                    }
                }
            }
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


        //private static void ApiOnReceiveError(object sender, ReceiveErrorEventArgs receiveErrorEventArgs)
        //{
        //    if (!Api.IsReceiving)
        //    {
        //        Api.StartReceiving();
        //    }
        //    var e = receiveErrorEventArgs.ApiRequestException;
        //    using (var sw = System.IO.File.AppendText(Path.Combine(RootDirectory, "..\\Logs\\apireceiveerror.log")))
        //    {
        //        sw.WriteLine($"{DateTime.Now} {e.ErrorCode} - {e.Message}\n{e.Source}");
        //    }
                
        //}

        internal static async Task<Message> Send(string message, long id, bool clearKeyboard = false,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Markdown)
        {
            try
            {
                MessagesSent++;
                //message = message.Replace("`",@"\`");
                if (clearKeyboard)
                {
                    var menu = new ReplyKeyboardHide { HideKeyboard = true };
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
            catch (AggregateException e)
            {
                return null;
            }

        }
        internal static async Task<Message> Send(string message, Update chatUpdate, bool clearKeyboard = false,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Markdown)
        {
            MessagesSent++;
            var id = chatUpdate.Message.Chat.Id;
            //message = message.Replace("`",@"\`");
            if (clearKeyboard)
            {
                var menu = new ReplyKeyboardHide { HideKeyboard = true };
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

        internal static async Task<Message> SendReply(string message, Message msg)
        {
            return await Api.SendTextMessageAsync(msg.Chat.Id, message, replyToMessageId: msg.MessageId, parseMode: ParseMode.Markdown);
        }
        internal static async Task<Message> SendReply(string message, long chatid, int msgid)
        {
            return await Api.SendTextMessageAsync(chatid, message, replyToMessageId: msgid, parseMode: ParseMode.Markdown);
        }
        internal static async Task<Message> SendReply(string message, Update msg)
        {
            return await Api.SendTextMessageAsync(msg.Message.Chat.Id, message, replyToMessageId: msg.Message.MessageId, parseMode: ParseMode.Markdown);
        }
        internal static async Task<Message> SendReply(string message, Update msg, InlineKeyboardMarkup keyboard)
        {
            return await Api.SendTextMessageAsync(msg.Message.Chat.Id, message, replyToMessageId: msg.Message.MessageId, replyMarkup:keyboard, parseMode: ParseMode.Markdown);
        }
    }

    internal static class Redis
    {
        private static string key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey("SOFTWARE\\Werewolf").GetValue("RedisPass").ToString();
        static ConnectionMultiplexer redis = ConnectionMultiplexer.Connect($"138.201.172.150:6379, password={key}");        
        public static IDatabase db = redis.GetDatabase(Constants.EnforcerDb);
    }
}
