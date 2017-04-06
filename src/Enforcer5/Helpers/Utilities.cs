using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
        private static System.Threading.Timer _apiWatch;
        public static int MessagesSent = 0;
        public static bool testing = false;
        public static string CurrentStatus = "";
        internal static string RootDirectory
        {
            get
            {
                var path = AppContext.BaseDirectory;;
                return Path.GetDirectoryName(path);
            }
        }
        internal delegate Task ChatCommandMethod(Update u, string[] args);

        internal delegate Task ChatCallbackMethod(CallbackQuery u, string[] args);
        internal static List<Models.Commands> Commands = new List<Models.Commands>();
        internal static List<Models.CallBacks> CallBacks = new List<Models.CallBacks>();
        internal static string LanguageDirectory => Path.GetFullPath(Path.Combine(RootDirectory, @"..\..\..\Languages"));
        internal static string TempLanguageDirectory => Path.GetFullPath(Path.Combine(RootDirectory, @"..\..\TempLanguageFiles"));
        public static async void Initialize(string updateid = null)
        {

            //get api token from registry
#if normal
            var key =
                    RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey("SOFTWARE\\Werewolf");
            TelegramAPIKey = key.GetValue("EnforcerAPI").ToString();
            if (Bot.TelegramAPIKey.Equals("279558316:AAGl5Nu_PNSGfDWLYEiC6Qt9VRSt1xLUIzY"))
            {
                testing = true;
            }
#endif
#if premium
            var key =
                    RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey("SOFTWARE\\Werewolf");
            TelegramAPIKey = key.GetValue("EnforcerPremiumAPI").ToString();
#endif

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
                        c.UploadAdmin = ca.UploadAdmin;
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
                        c.UploadAdmin = ca.UploadAdmin;
                        CallBacks.Add(c);
                    }
                }
            }

            Api.OnInlineQuery += UpdateHandler.InlineQueryReceived;
            Api.OnUpdate += UpdateHandler.UpdateReceived;
            Api.OnCallbackQuery += UpdateHandler.CallbackHandler;
            Api.OnReceiveError += ApiOnReceiveError;
            Api.OnReceiveGeneralError += ApiOnReceiveGenError;          
            Me = Api.GetMeAsync().Result;
            Api.PollingTimeout = TimeSpan.FromSeconds(1);
            Console.Title += " " + Me.Username;
            StartTime = DateTime.UtcNow;

            if (!testing)
            {
#if premium
                var offset = Redis.db.StringGetAsync("bot:last_Premium_update").Result;
#endif
#if normal
                var offset = Redis.db.StringGetAsync("bot:last_update").Result;
#endif
                if (offset.HasValue && offset.IsInteger)
                    Api.MessageOffset = (int)offset + 1;
                Console.WriteLine($" database offset is {offset}");
                //now we can start receiving   

            }           
            Api.StartReceiving();
            Console.WriteLine($"Starting ID = {Api.MessageOffset}");
            var wait = TimeSpan.FromSeconds(5);
            _apiWatch = new System.Threading.Timer(WatchAPI, null, wait, wait);

        }

        private static void ApiOnUpdate(object sender, UpdateEventArgs e)
        {
            Console.ForegroundColor = ConsoleColor.Green;
        }

        private static void ApiOnReceiveError(object sender, ReceiveErrorEventArgs receiveErrorEventArgs)
        {
            if (!Api.IsReceiving)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("--Not getting updates--");
                Api.StartReceiving();
            }
            var e = receiveErrorEventArgs.ApiRequestException;
                Console.WriteLine($"{DateTime.Now} {e.ErrorCode} - {e.Message}\n{e.Source}\n{e.StackTrace}\\n{sender.ToString()} Offset = {Api.MessageOffset}");
            var offset = Api.MessageOffset;
            Api.MessageOffset = offset + 1;

        }

        private static void ApiOnReceiveGenError(object sender, ReceiveGeneralErrorEventArgs receiveErrorEventArgs)
        {
            if (!Api.IsReceiving)
            {
                Api.StartReceiving();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("--Not getting updates--");
            }
            var e = receiveErrorEventArgs.Exception;
            Console.WriteLine($"{DateTime.Now} {e.Source} - {e.Message}\n{e.Source}\n{e.StackTrace}\n{sender.ToString()} Offset = {Api.MessageOffset}");
            var offset = Api.MessageOffset;
            Api.MessageOffset = offset + 1;

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
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html)
        {
            try
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
                    return await Api.SendTextMessageAsync(id, message, replyMarkup: customMenu,
                        disableWebPagePreview: true,
                        parseMode: parseMode);
                }
                else
                {
                    return await Api.SendTextMessageAsync(id, message, disableWebPagePreview: true, parseMode: parseMode);
                }
            }
            catch (ApiRequestException e)
            {
                if (e.ErrorCode == 400 && e.Message.Contains("Unsupported start tag"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        return await Api.SendTextMessageAsync(id, message, disableWebPagePreview: true, parseMode:ParseMode.Default);
                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                    }
                    finally
                    {
                        
                    }
                    return null;
                }
                if (e.ErrorCode == 400 && e.Message.Contains("message is too long"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    var messages = Regex.Split(message, "(.+?)(?:\r\n|\n)");
                    var amount = messages.Length / 4;
                    for (int j = 0; j < 4; j++)
                    {
                        var word = "";
                        for (int i = 0; i < amount; i++)
                        {
                            word = $"{word}\n{messages[i]}";
                        }
                        await Send(word, id);
                    }

                }
                try
                {
                    if (e.ErrorCode == 112)
                    {
                            return await Bot.Send("The markdown in this text is broken", id);
                    }
                    else if (e.ErrorCode == 403)
                    {
                        var lang = Methods.GetGroupLanguage(id).Doc;
                        var startMe = new Menu(1)
                        {
                            Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "StartMe"),
                                        url: $"https://t.me/{Bot.Me.Username}")
                                }
                        };
                        return await Bot.Send(Methods.GetLocaleString(lang, "botNotStarted"), id, customMenu:Key.CreateMarkupFromMenu(startMe));
                    }
                    else
                    {
                        await Bot.Send($"{e.ErrorCode}\n{e.Message}, 1231231", id);
                        return await Bot.Send($"2\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
                    }
                }
                catch (ApiRequestException ex)
                {
                    //fuckit 
                    return null;
                }
                catch (Exception exception)
                {
                    //fuckit
                    return null;
                }
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.InnerExceptions[0]}\n{e.StackTrace}");
                return null;
            }

        }
        internal static async Task<Message> Send(string message, Update chatUpdate, bool clearKeyboard = false,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html)
        {
            var id = chatUpdate.Message.Chat.Id;
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
            catch (ApiRequestException e)
            {
                if (e.ErrorCode == 400 && e.Message.Contains("Unsupported start tag"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        return await Api.SendTextMessageAsync(id, message, disableWebPagePreview: true, parseMode: ParseMode.Default);
                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                    }
                    finally
                    {

                    }
                    return null;
                }
                if (e.ErrorCode == 400 && e.Message.Contains("message is too long"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    var messages = Regex.Split(message, "(.+?)(?:\r\n|\n)");
                    var amount = messages.Length / 4;
                    for (int j = 0; j < 4; j++)
                    {
                        var word = "";
                        for (int i = 0; i < amount; i++)
                        {
                            word = $"{word}\n{messages[i]}";
                        }
                        await Send(word, chatUpdate);
                    }

                }
                try
                    {                        
                        if (e.ErrorCode == 112)
                        {
                            if (chatUpdate.Message != null && chatUpdate.Message.Chat.Title != null)
                            {
                                var lang = Methods.GetGroupLanguage(chatUpdate.Message).Doc;
                                return await Bot.SendReply(
                                    Methods.GetLocaleString(lang, "markdownBroken"), chatUpdate);
                            }
                            else
                            {
                                return await Bot.SendReply("The markdown in this text is broken", chatUpdate);
                            }
                        }
                        else if (e.ErrorCode == 403)
                        {
                            var lang = Methods.GetGroupLanguage(chatUpdate.Message).Doc;
                            var startMe = new Menu(1)
                            {
                                Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "StartMe"),
                                        url: $"https://t.me/{Bot.Me.Username}")
                                }
                            };
                            return await Bot.SendReply(Methods.GetLocaleString(lang, "botNotStarted"), chatUpdate, Key.CreateMarkupFromMenu(startMe));
                        }
                        else
                        {
                            await Bot.SendReply($"{e.ErrorCode}\n{e.Message}\n 12", chatUpdate);
                            return await Bot.Send($"3\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
                        }                        
                    }
                    catch (ApiRequestException ex)
                    {
                        //fuckit 
                        return null;
                    }
                    catch (Exception exception)
                    {
                        //fuckit
                        return null;
                    }
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.InnerExceptions[0]}\n{e.StackTrace}");
                return null;
            }

        }

        internal static async Task<Message> SendReply(string message, Message msg)
        {
            MessagesSent++;
            try
            {
                return await Api.SendTextMessageAsync(msg.Chat.Id, message, replyToMessageId: msg.MessageId, parseMode: ParseMode.Html, disableWebPagePreview: true);
            }
            catch (ApiRequestException e)
            {
                if (e.ErrorCode == 400 && e.Message.Contains("Unsupported start tag"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        return await Api.SendTextMessageAsync(msg.Chat.Id, message, disableWebPagePreview: true, parseMode: ParseMode.Default);
                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                    }
                    finally
                    {

                    }
                    
                }
                if (e.ErrorCode == 400 && e.Message.Contains("message is too long"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                        var messages = Regex.Split(message, "(.+?)(?:\r\n|\n)");
                        var amount = messages.Length / 4;
                        for (int j = 0; j < 4; j++)
                        {
                            var word = "";
                            for (int i = 0; i < amount; i++)
                            {
                                word = $"{word}\n{messages[i]}";
                            }
                            await SendReply(word, msg);
                        }

                }
                try
                {
                    if (e.ErrorCode == 112)
                    {
                        return await Bot.Send("The markdown in this text is broken", msg.Chat.Id);
                    }
                    else if (e.ErrorCode == 403)
                    {
                        var lang = Methods.GetGroupLanguage(msg.Chat.Id).Doc;
                        var startMe = new Menu(1)
                        {
                            Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "StartMe"),
                                        url: $"https://t.me/{Bot.Me.Username}")
                                }
                        };
                        return await Bot.Send(Methods.GetLocaleString(lang, "botNotStarted"), msg.Chat.Id, customMenu: Key.CreateMarkupFromMenu(startMe));
                    }
                    else
                    {
                        await Bot.Send($"{e.ErrorCode}\n{e.Message}, 1231231", msg.Chat.Id);
                        return await Bot.Send($"2\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
                    }
                }
                catch (ApiRequestException ex)
                {
                    //fuckit 
                    return null;
                }
                catch (Exception exception)
                {
                    //fuckit
                    return null;
                }
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.InnerExceptions[0]}\n{e.StackTrace}");
                return null;
            }
        }
        internal static async Task<Message> SendReply(string message, long chatid, int msgid)
        {
            MessagesSent++;
            try
            {
                return await Api.SendTextMessageAsync(chatid, message, replyToMessageId: msgid, parseMode: ParseMode.Html, disableWebPagePreview: true);
            }
            catch (ApiRequestException e)
            {
                if (e.ErrorCode == 400 && e.Message.Contains("Unsupported start tag"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        return await Api.SendTextMessageAsync(chatid, message, disableWebPagePreview: true, parseMode: ParseMode.Default);
                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                    }
                    finally
                    {

                    }
                    
                }
                if (e.ErrorCode == 400 && e.Message.Contains("message is too long"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        var messages = Regex.Split(message, "(.+?)(?:\r\n|\n)");
                        var amount = messages.Length / 4;
                        for (int j = 0; j < 4; j++)
                        {
                            var word = "";
                            for (int i = 0; i < amount; i++)
                            {
                                word = $"{word}\n{messages[i]}";
                            }
                            await SendReply(word, chatid, msgid);
                        }

                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                    }
                    finally
                    {

                    }

                }
                try
                {
                    if (e.ErrorCode == 112)
                    {
                        return await Bot.Send("The markdown in this text is broken", chatid);
                    }
                    else if (e.ErrorCode == 403)
                    {
                        var lang = Methods.GetGroupLanguage(chatid).Doc;
                        var startMe = new Menu(1)
                        {
                            Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "StartMe"),
                                        url: $"https://t.me/{Bot.Me.Username}")
                                }
                        };
                        return await Bot.Send(Methods.GetLocaleString(lang, "botNotStarted"), chatid, customMenu: Key.CreateMarkupFromMenu(startMe));
                    }
                    else
                    {
                        await Bot.Send($"{e.ErrorCode}\n{e.Message}, 1231231", chatid);
                        return await Bot.Send($"2\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
                    }
                }
                catch (ApiRequestException ex)
                {
                    //fuckit 
                    return null;
                }
                catch (Exception exception)
                {
                    //fuckit
                    return null;
                }
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.InnerExceptions[0]}\n{e.StackTrace}");
                return null;
            }
        }
        internal static async Task<Message> SendReply(string message, Update msg)
        {
            MessagesSent++;
            try
            {
                return await Api.SendTextMessageAsync(msg.Message.Chat.Id, message, replyToMessageId: msg.Message.MessageId, parseMode: ParseMode.Html, disableWebPagePreview: true);
            }
            catch (ApiRequestException e)
            {
                if (e.ErrorCode == 400 && e.Message.Contains("Unsupported start tag"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        return await Api.SendTextMessageAsync(msg.Message.Chat.Id, message, disableWebPagePreview: true, parseMode: ParseMode.Default);
                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                    }
                    finally
                    {

                    }
                   
                }
                if (e.ErrorCode == 400 && e.Message.Contains("message is too long"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        var messages = Regex.Split(message, "(.+?)(?:\r\n|\n)");
                        var amount = messages.Length / 4;
                        for (int j = 0; j < 4; j++)
                        {
                            var word = "";
                            for (int i = 0; i < amount; i++)
                            {
                                word = $"{word}\n{messages[i]}";
                            }
                            await SendReply(word, msg);
                        }

                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                    }
                    finally
                    {

                    }

                }
                try
                {
                    if (e.ErrorCode == 112)
                    {
                        return await Bot.Send("The markdown in this text is broken", msg.Message.Chat.Id);
                    }
                    else if (e.ErrorCode == 403)
                    {
                        var lang = Methods.GetGroupLanguage(msg.Message.Chat.Id).Doc;
                        var startMe = new Menu(1)
                        {
                            Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "StartMe"),
                                        url: $"https://t.me/{Bot.Me.Username}")
                                }
                        };
                        return await Bot.Send(Methods.GetLocaleString(lang, "botNotStarted"), msg.Message.Chat.Id, customMenu: Key.CreateMarkupFromMenu(startMe));
                    }
                    else
                    {
                        await Bot.Send($"{e.ErrorCode}\n{e.Message}, 1231231", msg.Message.Chat.Id);
                        return await Bot.Send($"2\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
                    }
                }
                catch (ApiRequestException ex)
                {
                    //fuckit 
                    return null;
                }
                catch (Exception exception)
                {
                    //fuckit
                    return null;
                }
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.InnerExceptions[0]}\n{e.StackTrace}");
                return null;
            }
        }
        internal static async Task<Message> SendReply(string message, Update msg, InlineKeyboardMarkup keyboard)
        {
            MessagesSent++;
            try
            {
                return await Api.SendTextMessageAsync(msg.Message.Chat.Id, message, replyToMessageId: msg.Message.MessageId, replyMarkup: keyboard, parseMode: ParseMode.Html, disableWebPagePreview: true);
            }
            catch (ApiRequestException e)
            {
                if (e.ErrorCode == 400 && e.Message.Contains("Unsupported start tag"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        return await Api.SendTextMessageAsync(msg.Message.Chat.Id, message, disableWebPagePreview: true, parseMode: ParseMode.Default);
                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                        return null;
                    }
                   
                }
                if (e.ErrorCode == 400 && e.Message.Contains("message is too long"))
                {
                    //Console.WriteLine($"HANDLED\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}");
                    try
                    {
                        var messages = Regex.Split(message, "(.+?)(?:\r\n|\n)");
                        var amount = messages.Length / 4;
                        for (int j = 0; j < 4; j++)
                        {
                            var word = "";
                            for (int i = 0; i < amount; i++)
                            {
                                word = $"{word}\n{messages[i]}";
                            }
                            await SendReply(word, msg);
                        }

                    }
                    catch (ApiRequestException ex)
                    {
                        Console.WriteLine($"HANDLED\n{ex.ErrorCode}\n\n{ex.Message}\n\n{ex.StackTrace}");
                    }
                    finally
                    {

                    }

                }
                try
                {
                    if (e.ErrorCode == 112)
                    {
                        return await Bot.Send("The markdown in this text is broken", msg.Message.Chat.Id);
                    }
                    else if (e.ErrorCode == 403)
                    {
                        var lang = Methods.GetGroupLanguage(msg.Message.Chat.Id).Doc;
                        var startMe = new Menu(1)
                        {
                            Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "StartMe"),
                                        url: $"https://t.me/{Bot.Me.Username}")
                                }
                        };
                        return await Bot.Send(Methods.GetLocaleString(lang, "botNotStarted"), msg.Message.Chat.Id, customMenu: Key.CreateMarkupFromMenu(startMe));
                    }
                    else
                    {
                        await Bot.Send($"{e.ErrorCode}\n{e.Message}, 1231231", msg.Message.Chat.Id);
                        return await Bot.Send($"2\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
                    }
                }
                catch (ApiRequestException ex)
                {
                    //fuckit 
                    return null;
                }
                catch (Exception exception)
                {
                    //fuckit
                    return null;
                }
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.InnerExceptions[0]}\n{e.StackTrace}");
                return null;
            }
        }

        private static void WatchAPI(object state)
        {
            while (!Api.IsReceiving)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("--Not getting updates--");
                Api.StartReceiving();
            }
        }
    }

    internal static class Redis
    {
        private static string key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey("SOFTWARE\\Werewolf").GetValue("RedisPass").ToString();
        static ConnectionMultiplexer redis = ConnectionMultiplexer.Connect($"138.201.172.150:6379, password={key}, allowAdmin=true");        
        public static IDatabase db = redis.GetDatabase(Constants.EnforcerDb);

        public static void SaveRedis()
        {
            redis.GetServer($"138.201.172.150:6379").Save(SaveType.BackgroundSave);
        }

        public static bool Start()
        {
            try
            {
                var res = db.StringSet("testWrite", "trying");
                return res;
            }
            catch (Exception e)
            {
                return false;
            }
            
        }
    }
}
