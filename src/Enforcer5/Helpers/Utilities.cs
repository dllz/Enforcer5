using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Handlers;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Newtonsoft.Json;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using System.Linq;
using Telegram.Bot.Types.Payments;

#pragma warning disable CS0168
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
        internal delegate void ChatCommandMethod(Update u, string[] args);
        internal delegate void ChatCallbackMethod(CallbackQuery u, string[] args);
        internal delegate List<InlineQueryResultArticle> InlineQuery(User user, string[] args, XDocument lang);
        internal static HashSet<Models.Commands> Commands = new HashSet<Models.Commands>();
        internal static HashSet<Models.CallBacks> CallBacks = new HashSet<Models.CallBacks>();
        internal static HashSet<Models.Queries> Queries = new HashSet<Models.Queries>();
        internal static string LanguageDirectory => RegHelper.GetPath("LanguagesPath", "Languages");
        internal static string TempLanguageDirectory => RegHelper.GetPath("TempLanguageFilesPath", "TempLanguageFiles");
        internal static string LogDirectory => RegHelper.GetPath("LogPath", "Logs");

        /// <summary>Chat that startup notices and unhandled errors are reported to.</summary>
        internal static long ErrorChatId => RegHelper.GetLong("ErrorChatId") ?? Constants.Devs[0];

        /// <summary>Redis key holding the last processed update id. Separate per edition.</summary>
#if PREMIUM
        private const string OffsetKey = "bot:last_Premium_update";
#else
        private const string OffsetKey = "bot:last_update";
#endif

        private static readonly CancellationTokenSource _receiverCts = new CancellationTokenSource();
        internal static CancellationToken ShutdownToken => _receiverCts.Token;
        internal static void StopReceiving() => _receiverCts.Cancel();

        public static async Task Initialize(string updateid = null)
        {
#if PREMIUM
            TelegramAPIKey = RegHelper.GetRequired("EnforcerPremiumAPI");
#else
            TelegramAPIKey = RegHelper.GetRequired("EnforcerAPI");
#endif
            var serverUrl = RegHelper.GetRegValue("TelegramServerUrl");
            Api = string.IsNullOrEmpty(serverUrl)
                ? new TelegramBotClient(TelegramAPIKey)
                : new TelegramBotClient(new TelegramBotClientOptions(TelegramAPIKey, serverUrl));

            Me = await Api.GetMe();
            try { Console.Title = $"Enforcer {Me.Username}"; } catch (PlatformNotSupportedException) { }
            LogHelper.Info($"Connected as @{Me.Username}");
            StartTime = DateTime.UtcNow;

            // Resume from the offset we persisted, so a restart neither reprocesses nor skips updates.
            long offset;
            var storedOffset = Redis.db.StringGetAsync(OffsetKey).Result;
            if (!storedOffset.HasValue || !long.TryParse(storedOffset.ToString(), out offset))
                offset = 0;

            LogHelper.Info($"Database offset is {offset}");
            Send($"Bot Started:\n{Methods.DisplayNow():hh:mm:ss dd-MM-yyyy}", ErrorChatId);
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

            foreach (var m in typeof(Queries).GetMethods())
            {
                var c = new Models.Queries();
                foreach (var a in m.GetCustomAttributes(true))
                {
                    if (a is Attributes.Query)
                    {
                        var ca = a as Attributes.Query;
                        c.Trigger = ca.Trigger;
                        c.DefaultResponse = ca.DefaultResponse;
                        c.Description = ca.Description;
                        c.Title = ca.Title;
                        c.Method = (InlineQuery) m.CreateDelegate(typeof(InlineQuery));
                        Queries.Add(c);
                    }
                }
            }
            // Now we can start receiving. ReceiveAsync runs the long-polling loop until cancelled;
            // it restarts internally on transient errors, so no watchdog timer is needed.
            LogHelper.Info($"Starting ID = {offset + 1}");
            var receiverOptions = new ReceiverOptions
            {
                Offset = (int)(offset + 1),
                AllowedUpdates = new[]
                {
                    UpdateType.Message,
                    UpdateType.CallbackQuery,
                    UpdateType.InlineQuery,
                    UpdateType.PreCheckoutQuery,
                    UpdateType.ShippingQuery
                }
            };

            await Api.ReceiveAsync(new EnforcerUpdateHandler(), receiverOptions, ShutdownToken);
        }

        /// <summary>
        /// Routes polled updates to the existing handlers. Replaces the 13.x
        /// OnUpdate/OnInlineQuery/OnCallbackQuery event model.
        /// </summary>
        private sealed class EnforcerUpdateHandler : IUpdateHandler
        {
            public Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
            {
                switch (update.Type)
                {
                    case UpdateType.CallbackQuery:
                        UpdateHandler.CallbackHandler(update.CallbackQuery);
                        break;
                    case UpdateType.InlineQuery:
                        UpdateHandler.InlineQueryReceived(update.InlineQuery);
                        break;
                    default:
                        UpdateHandler.UpdateReceived(update);
                        break;
                }
                return Task.CompletedTask;
            }

            public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source,
                CancellationToken cancellationToken)
            {
                if (exception is ApiRequestException api)
                    LogHelper.Error($"Polling [{source}] {api.ErrorCode}: {api.Message}");
                else
                    LogHelper.Error($"Polling [{source}]: {exception.Message}\n{exception.StackTrace}");

                // A failing poll must not turn into a tight loop.
                if (source == HandleErrorSource.PollingError)
                    return Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return Task.CompletedTask;
            }
        }

        internal static void ReplyToCallback(CallbackQuery query, string text = null, bool edit = true, bool showAlert = false, InlineKeyboardMarkup replyMarkup = null)
        {
            //first answer the callback
            Bot.Api.AnswerCallbackQuery(query.Id, edit ? null : text, showAlert);
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
            return Bot.Api.EditMessageText(id, msgId, text, replyMarkup: replyMarkup);
        }

        /// <summary>
        /// Unwraps the ApiRequestException from a blocking call's AggregateException, if there is one.
        /// Every send path here is synchronous (.Result), so Telegram errors arrive wrapped.
        /// </summary>
        internal static ApiRequestException AsApiError(Exception e)
        {
            if (e is ApiRequestException direct) return direct;
            if (e is AggregateException agg)
                return agg.InnerExceptions.OfType<ApiRequestException>().FirstOrDefault();
            return e?.InnerException as ApiRequestException;
        }

        /// <summary>True when Telegram rejected the send because the bot cannot reach that user or chat.</summary>
        private static bool IsUnreachable(ApiRequestException api)
        {
            if (api == null) return false;
            // 403: bot blocked, kicked, or user deactivated. 400 + these descriptions: chat gone.
            if (api.ErrorCode == 403) return true;
            return api.ErrorCode == 400 &&
                   (api.Message.Contains("chat not found") || api.Message.Contains("user is deactivated"));
        }

        /// <summary>True when the message body failed Telegram's HTML/Markdown parser.</summary>
        private static bool IsParseFailure(ApiRequestException api)
        {
            return api != null && api.ErrorCode == 400 && api.Message.Contains("can't parse entities");
        }

        /// <summary>Sleeps for the interval Telegram asked for on a 429, or a sane default.</summary>
        private static void WaitOutRateLimit(ApiRequestException api)
        {
            var seconds = api?.Parameters?.RetryAfter ?? 5;
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(seconds, 60)));
        }

        private static Message CatchSend(string message, long id, InlineKeyboardMarkup customMenu = null,
            ParseMode parsemode = ParseMode.None, int messageId = -1)
        {
            Message result = null;
            try
            {
                MessagesSent++;
                result = Bot.Api.SendMessage(id, message, parsemode,
                    replyParameters: messageId == -1 ? null : new ReplyParameters { MessageId = messageId },
                    replyMarkup: customMenu).Result;
            }
            catch (Exception e)
            {
                // Last-resort path: never recurse back into the error channel.
                LogHelper.Error($"CatchSend to {id} failed: {AsApiError(e)?.Message ?? e.Message}");
            }
            return result;
        }

        internal static Message SendToPm(string message, long pmId, long chatId, InlineKeyboardMarkup menu = null,
            ParseMode parseMode = ParseMode.Html, int messageId = -1)
        {
            Message result = null;
            try
            {
                result = Send(message, pmId, menu, parseMode);
            }
            catch (Exception e) when (IsUnreachable(AsApiError(e)))
            {
                // The user has not started the bot (or has blocked it) - point them at it in the group.
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var startMe = new Menu(1)
                {
                    Buttons = new List<InlineButton>
                    {
                        new InlineButton(Methods.GetLocaleString(lang, "StartMe"), url:$"https://t.me/{Bot.Me.Username}")
                    }
                };
                if (messageId == -1)
                    Bot.Send(Methods.GetLocaleString(lang, "botNotStarted"), chatId, Key.CreateMarkupFromMenu(startMe));
                else
                    Bot.SendReply(Methods.GetLocaleString(lang, "botNotStarted"), chatId, messageId, Key.CreateMarkupFromMenu(startMe));
                result = null;
            }
            catch (Exception)
            {
                result = CatchSend(message, chatId, menu, parseMode, messageId);
            }
            return result;
        }

        internal static Message SendToPm(string message, Update update, InlineKeyboardMarkup menu = null,
            ParseMode parseMode = ParseMode.Html)
        {
            return SendToPm(message, update.Message.From.Id, update.Message.Chat.Id, menu, parseMode,
                update.Message.MessageId);
        }

        internal static Message Send(string message, long id,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html, [CallerMemberName]string parentMethod = "")
        {
            Message result = null;
            try
            {
                MessagesSent++;
                result = Api.SendMessage(id, message, parseMode,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyMarkup: customMenu).Result;
            }
            catch (Exception e)
            {
                var api = AsApiError(e);

                if (api?.ErrorCode == 429)
                {
                    WaitOutRateLimit(api);
                    result = CatchSend(message, id, customMenu, parseMode);
                }
                else if (IsParseFailure(api))
                {
                    // Send it unformatted rather than losing the message entirely.
                    result = CatchSend(message, id, customMenu, ParseMode.None);
                }
                else if (api != null && api.Message.Contains("message is too long"))
                {
                    result = SendInChunks(message, id);
                }
                else if (IsUnreachable(api))
                {
                    // SendToPm needs to know so it can prompt the user to start the bot.
                    if (parentMethod.Equals("SendToPm")) throw;
                    result = null;
                }
                else if (api != null && api.Message.Contains("bot can't send messages to bots"))
                {
                    result = null;
                }
                else
                {
                    LogHelper.Error($"Send to {id} failed: {api?.Message ?? e.Message}");
                    result = null;
                }
            }
            return result;
        }

        /// <summary>
        /// Splits an over-long message on line boundaries and sends each part.
        /// Returns the last message sent, or null if nothing went out.
        /// </summary>
        private static Message SendInChunks(string message, long id, int replyToMessageId = -1)
        {
            const int MaxLength = 4000; // Telegram's limit is 4096; leave room for formatting.
            Message last = null;
            var lines = message.Split('\n');
            var chunk = new StringBuilder();

            foreach (var line in lines)
            {
                if (chunk.Length + line.Length + 1 > MaxLength && chunk.Length > 0)
                {
                    last = CatchSend(chunk.ToString(), id, messageId: replyToMessageId);
                    chunk.Clear();
                }
                chunk.Append(line).Append('\n');
            }
            if (chunk.Length > 0)
                last = CatchSend(chunk.ToString(), id, messageId: replyToMessageId);

            return last;
        }
        internal static Message Send(string message, Update chatUpdate,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html, [CallerMemberName]string parentMethod = "")
        {
            var id = chatUpdate.Message.Chat.Id;
            return Send(message, id, customMenu, parseMode, parentMethod);
        }

        internal static Message SendReply(string message, Message msg, [CallerMemberName]string parentMethod = "")
        {
            return SendReply(message, msg.Chat.Id, msg.MessageId, parentMethod: parentMethod);
        }
        internal static Message SendReply(string message, long chatid, int msgid, InlineKeyboardMarkup keyboard = null, [CallerMemberName]string parentMethod = "")
        {
            MessagesSent++;
            Message result = null;
            try
            {
                result = Api.SendMessage(chatid, message, ParseMode.Html,
                    replyParameters: new ReplyParameters { MessageId = msgid },
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyMarkup: keyboard).Result;
            }
            catch (Exception e)
            {
                var api = AsApiError(e);

                if (api?.ErrorCode == 429)
                {
                    WaitOutRateLimit(api);
                    result = CatchSend(message, chatid, keyboard, ParseMode.Html, msgid);
                }
                else if (IsParseFailure(api))
                {
                    result = CatchSend(message, chatid, keyboard, ParseMode.None, msgid);
                }
                else if (api != null && api.Message.Contains("reply message not found"))
                {
                    // The message being replied to was deleted - send it standalone.
                    result = Send(message, chatid);
                }
                else if (api != null && api.Message.Contains("message is too long"))
                {
                    result = SendInChunks(message, chatid, msgid);
                }
                else if (IsUnreachable(api))
                {
                    if (parentMethod.Equals("SendToPm")) throw;
                    result = null;
                }
                else
                {
                    LogHelper.Error($"SendReply to {chatid} failed: {api?.Message ?? e.Message}");
                    result = null;
                }
            }
            return result;
        }
        internal static Message SendReply(string message, Update msg,[CallerMemberName]string parentMethod = "")
        {            
            return SendReply(message, msg.Message.Chat.Id, msg.Message.MessageId, parentMethod:parentMethod);
        }
        internal static Message SendReply(string message, Update msg, InlineKeyboardMarkup keyboard, [CallerMemberName]string parentMethod = "")
        {            
            return SendReply(message, msg.Message.Chat.Id, msg.Message.MessageId, keyboard, parentMethod);
        }
       

        public static Boolean DeleteMessage(long chatId, int msgid)
        {
            try
            {
                Bot.Api.DeleteMessage(chatId, msgid).Wait();
                return true;
            }
            catch (Exception e)
            {
                // Already gone is success as far as callers are concerned.
                var api = AsApiError(e);
                if (api != null && api.Message.ToLower().Contains("message to delete not found")) return true;
                throw;
            }
        }

        public static void DeleteLastWelcomeMessage(long ChatId, int msgid)
        {
            var enabled = Redis.db.HashGetAsync($"chat:{ChatId}:settings", "DeleteLastWelcome").Result;
            int lastWelcomeMessageId = (int)Redis.db.StringGetAsync($"chat:{ChatId}:lastwelcome").Result;
            if (enabled.Equals("no"))
            {
                
                Redis.db.StringSetAsync($"chat:{ChatId}:lastwelcome", $"{msgid}");
                DeleteMessage(ChatId, lastWelcomeMessageId);
            }  
        }

        public static void SendInvoice(long userId, string title, string description, string callbackKey, LabeledPrice[] labeledPrices)
        {
            try
            {
                Api.SendInvoice(userId, title, description,
                    payload: Guid.NewGuid().ToString(),
                    currency: Constants.paymentCurrency,
                    prices: labeledPrices,
                    providerToken: Constants.PaymentProviderToken,
                    needEmail: true).Wait();
            }
            catch (Exception e)
            {
                LogHelper.Error($"Failed to send invoice to {userId}: {AsApiError(e)?.Message ?? e.Message}");
                Bot.CatchSend("Sorry, something went wrong creating your invoice. Please try again.", userId);
            }
        }

        /// <summary>Permissions granted to a fully muted user - everything off.</summary>
        private static readonly ChatPermissions MutedPermissions = new ChatPermissions
        {
            CanSendMessages = false,
            CanSendAudios = false,
            CanSendDocuments = false,
            CanSendPhotos = false,
            CanSendVideos = false,
            CanSendVideoNotes = false,
            CanSendVoiceNotes = false,
            CanSendPolls = false,
            CanSendOtherMessages = false,
            CanAddWebPagePreviews = false,
            CanChangeInfo = false,
            CanInviteUsers = false,
            CanPinMessages = false,
            CanManageTopics = false
        };

        public static bool Mute(long chatId, long userId, DateTime untilDatetime = default(DateTime))
        {
            try
            {
                Bot.Api.RestrictChatMember(chatId, userId, MutedPermissions,
                    untilDate: untilDatetime == default(DateTime) ? (DateTime?)null : untilDatetime).Wait();
                return true;
            }
            catch (Exception e)
            {
                LogHelper.Error($"Mute failed in {chatId} for {userId}: {AsApiError(e)?.Message ?? e.Message}");
                return false;
            }
        }

        public static bool Unmute(long chatId, long userId)
        {
            try
            {
                // Restore the user to whatever the group's own default permissions are.
                var chatPermissions = Bot.Api.GetChat(chatId).Result.Permissions ?? new ChatPermissions
                {
                    CanSendMessages = true,
                    CanSendAudios = true,
                    CanSendDocuments = true,
                    CanSendPhotos = true,
                    CanSendVideos = true,
                    CanSendVideoNotes = true,
                    CanSendVoiceNotes = true,
                    CanSendOtherMessages = true,
                    CanAddWebPagePreviews = true
                };
                Bot.Api.RestrictChatMember(chatId, userId, chatPermissions).Wait();
                return true;
            }
            catch (Exception e)
            {
                LogHelper.Error($"Unmute failed in {chatId} for {userId}: {AsApiError(e)?.Message ?? e.Message}");
                return false;
            }
        }
    }

    internal static class Redis
    {
        private static ConnectionMultiplexer _redis;
        private static IDatabase _db;

        /// <summary>
        /// Connection is established lazily by Start(). It must not happen in a static field
        /// initializer: a failure there surfaces as a TypeInitializationException from whichever
        /// unrelated line first touches this class, with nothing logged.
        /// </summary>
        public static IDatabase db
        {
            get
            {
                if (_db == null)
                    throw new InvalidOperationException("Redis.Start() must succeed before the database is used.");
                return _db;
            }
        }

        public static void SaveRedis()
        {
            var endpoint = _redis.GetEndPoints().FirstOrDefault();
            if (endpoint == null) return;
            _redis.GetServer(endpoint).Save(SaveType.BackgroundSave);
        }

        public static bool Start()
        {
            try
            {
                if (_redis == null || !_redis.IsConnected)
                {
                    var options = ConfigurationOptions.Parse(RegHelper.GetRequired("RedisConnection"));
                    var password = RegHelper.GetRegValue("RedisPassword");
                    if (!string.IsNullOrEmpty(password))
                        options.Password = password;
                    options.AllowAdmin = true;          // required for the BGSAVE in SaveRedis()
                    options.AbortOnConnectFail = false; // survive Redis restarting under us

                    _redis = ConnectionMultiplexer.Connect(options);
                    _db = _redis.GetDatabase(Constants.EnforcerDb);
                }

                return _db.StringSet("testWrite", "trying");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ERROR] Redis connection failed: {e.Message}");
                return false;
            }
        }
    }
}
