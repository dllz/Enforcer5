using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection.Emit;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Helpers;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot;
#pragma warning disable CS4014
namespace Enforcer5
{ 
    public static class Service
    {
        public static void Welcome(Message message)
        {
            var chatId = message.Chat.Id;
            // Nobody is welcomed into a raid: the joiners are its raiders, or muted until it is over.
            if (Raids.RaidGuard.Quiet(chatId)) return;
            var config = GroupConfigs.For(chatId);
            if (!config.Flag(ConfigFlag.Welcome))
                return;
            // Once per join: the service message and the member update both tell of it.
            if (message.NewChatMember is { } joiner && !ClaimWelcome(chatId, joiner.Id)) return;
            var msgs = Redis.db.StringGetAsync($"spam:added:{message.Chat.Id}").Result;
            var defSpamValue = 1;
            var maxTime = TimeSpan.FromMinutes(1);
            int msg = 0;
            if (msgs.HasValue && !string.IsNullOrEmpty(msgs))
            {
                try
                {
                    msg = int.Parse(msgs);
                    if (msg == 0)
                    {
                        msg = 1;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading the join counter of {message.Chat.Id}");
                }

            }
            else
            {
                msg = 1;
            }
    Redis.db.StringSetAsync($"spam:added:{message.Chat.Id}", msg + 1, maxTime);
            if (msg >= defSpamValue+1)
            {
               return; 
            }
var joinSpam = Redis.db.StringGetAsync($"spam:added:{message.Chat.Id}:{message.NewChatMember.Id}").Result;
defSpamValue = 3;
            maxTime = TimeSpan.FromMinutes(5);
            if (joinSpam.HasValue && !string.IsNullOrEmpty(joinSpam))
            {
                try
                {
                    msg = int.Parse(joinSpam);
                    if (msg == 0)
                    {
                        msg = 1;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading the join counter of {message.NewChatMember.Id} in {message.Chat.Id}");
                }
                
            }
            else
            {
                msg = 1;
            }
            Redis.db.StringSetAsync($"spam:added:{message.Chat.Id}:{message.NewChatMember.Id}", msg + 1, maxTime);
            if (msg >= defSpamValue + 1)
            {
                return;
            }
            joinSpam = Redis.db.StringGetAsync($"spam:added:{message.NewChatMember.Id}").Result;
            defSpamValue = 3;
            maxTime = TimeSpan.FromMinutes(30);
            if (joinSpam.HasValue && !string.IsNullOrEmpty(joinSpam))
            {
                try
                {
                    msg = int.Parse(joinSpam);
                    if (msg == 0)
                    {
                        msg = 1;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading the join counter of {message.NewChatMember.Id}");
                }
            }
            else
            {
                msg = 1;
            }
             Redis.db.StringSetAsync($"spam:added:{message.NewChatMember.Id}", msg + 1, maxTime);
            if (msg >= defSpamValue + 1)
            {
                return;
            }
            switch (message.NewChatMember.Id)
            {
                case 125311351://Daniel
#if PREMIUM
                    Bot.Api.SendDocument(message.Chat.Id, InputFile.FromFileId("CgADBAADZCgAApwaZAfDe5MFy-IHCAI"));
#endif
#if NORMAL
                    Bot.Api.SendDocument(message.Chat.Id, InputFile.FromFileId("CgADBAADZCgAApwaZAfmRkSuLkIV9AI"));
#endif
                    break;
                case 36702373://Karma (Prize Winner)
                    Bot.Send("Let the players play, let the haters hate. And I, Karma, will handle their fate.\nRemember: Karma's gonna come collect your debt.", chatId);
                    break;
                default:
                    // The group's welcome (its config since rollout step 15; LegacyGroupConfig read the old hash).
                    var welcome = config.Welcome;
                    var type = welcome.Type;
                    var content = welcome.Content;
                    if (type == "media")
                    {
                        var fileId = welcome.MediaFileId;
                        // No GIF was ever saved: nothing to welcome with, and nothing for anyone to fix.
                        if (fileId == null) return;
                        Message response = Bot.Api.SendDocument(message.Chat.Id, InputFile.FromFileId(fileId)).Result;

                        Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                    }
                    else if (type == "custom")
                    {
                        var file = welcome.MediaFileId;
                        var html = WelcomeText.Html(content ?? "", message.NewChatMember, message.Chat.Title);
                        var plain = WelcomeText.Plain(content ?? "", message.NewChatMember, message.Chat.Title);
                        if (file != null)
                        {
                            Message response = SendWelcomeGif(message.Chat.Id, file, html, plain);

                            Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                        }
                        else
                        {
                            Message response = Bot.Send(html, message.Chat.Id, plainText: plain);

                            // Null when a 429 deferred it.
                            if (response != null) Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                        }
                    }
                    // A composed welcome without content: never set up, and never sent (as before).
                    else if (type == "composed" && content != null)
                    {
                        XDocument lang;
                        try
                        {
                            lang = Methods.GetGroupLanguage(message,true).Doc;
                        }
                        catch (NullReferenceException e)
                        {
                            ErrorReporter.Report(e, $"Loading the language of {message.Chat.Id} for the welcome, using the default");
                            try
                            {
                                lang = Methods.GetGroupLanguage(Bot.ErrorChatId).Doc;
                            }
                            catch (NullReferenceException exception)
                            {
                                ErrorReporter.Report(exception, "Loading the default language for the welcome");
                                return;
                            }
                        }
                        // The member's name links to their profile (Daniel, 2026-11-03). Sent as HTML: the rules,
                        // about and admin list below are HTML, and showed their tags while this went out plain.
                        var greeting = DefaultWelcome(lang, message);
                        if (!content.Equals("no"))
                        {
                            var text = greeting;
                            switch (content)
                            {
                                case "a":
                                    text = $"{text}\n\n{Methods.GetAbout(message.Chat.Id, lang)}";
                                    break;
                                case "r":
                                    text = $"{text}\n\n{Methods.GetRules(message.Chat.Id, lang)}";
                                    break;
                                case "m":
                                    text = $"{text}\n\n{Methods.GetAdminList(message, lang)}";
                                    break;
                                case "ra":
                                    text = $"{text}\n\n{Methods.GetAbout(message.Chat.Id, lang)}\n{Methods.GetRules(message.Chat.Id, lang)}";
                                    break;
                                case "am":
                                    text = $"{text}\n\n{Methods.GetAbout(message.Chat.Id, lang)}\n{Methods.GetAdminList(message, lang)}";
                                    break;
                                case "rm":
                                    text = $"{text}\n\n{Methods.GetRules(message.Chat.Id, lang)}\n{Methods.GetAdminList(message, lang)}";
                                    break;
                                case "ram":
                                    text = $"{text}\n\n{Methods.GetAbout(message.Chat.Id, lang)}\n{Methods.GetRules(message.Chat.Id, lang)}\n{Methods.GetAdminList(message, lang)}";
                                    break;
                            }
                            // If the group's rules or about do not parse, the text goes out without its markup.
                            Message response = Bot.Send(text, message.Chat.Id, plainText: WelcomeText.Unformat(text));

                            if (response != null) Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                        }
                        else
                        {
                            Message response = Bot.Send(greeting, message.Chat.Id, plainText: WelcomeText.Unformat(greeting));

                            if (response != null) Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                        }
                    }
                    break;
            }            
        }

        /// <summary>How long a join's welcome waits for its service message, which a small group gets.</summary>
        internal static readonly TimeSpan WelcomeWaitsForServiceMessage = TimeSpan.FromSeconds(3);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long Chat, long User), DateTime> Welcomed = new();

        internal static void ResetWelcomesForTests() => Welcomed.Clear();

        private static bool ClaimWelcome(long chatId, long userId)
        {
            var now = Helpers.Clock.UtcNow;
            var keep = TimeSpan.FromMinutes(10);
            var welcome = (chatId, userId);
            // TryAdd/TryUpdate: AddOrUpdate's factories can run and lose the race, claiming twice.
            bool claimed;
            while (true)
            {
                if (Welcomed.TryAdd(welcome, now)) { claimed = true; break; }
                if (!Welcomed.TryGetValue(welcome, out var at)) continue;
                if (now - at < keep) { claimed = false; break; }
                if (Welcomed.TryUpdate(welcome, now, at)) { claimed = true; break; }
            }
            if (claimed && Welcomed.Count > 20_000)
                foreach (var (key, at) in Welcomed)
                    if (now - at >= keep) Welcomed.TryRemove(key, out _);
            return claimed;
        }

        /// <summary>
        /// Someone joined, as a member update says. Telegram sends no "joined" service message in big groups
        /// (the support group, The Origin: no welcome since long before October 2026), so the welcome comes from
        /// here when no service message brought it within <see cref="WelcomeWaitsForServiceMessage"/>.
        /// </summary>
        internal static void WelcomeFromMemberUpdate(Chat chat, User joiner, User from)
        {
            if (joiner == null || joiner.IsBot || chat.Type is not (ChatType.Group or ChatType.Supergroup)) return;
            Bot.Dispatch(async () =>
            {
                await Helpers.Clock.Delay(WelcomeWaitsForServiceMessage);
                if (Welcomed.ContainsKey((chat.Id, joiner.Id))) return;
                if (!await Handlers.UpdateHandler.ServesAsync(chat.Id)) return;
                var message = new Message
                {
                    Chat = chat, From = from ?? joiner, Date = Helpers.Clock.UtcNow, NewChatMembers = new[] { joiner }
                };
                Bot.DispatchAction(() =>
                {
                    try
                    {
                        Welcome(message);
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, $"Welcoming {joiner.Id} to {chat.Id} from a member update");
                    }
                }, "WelcomeFromMemberUpdate");
            }, "WelcomeFromMemberUpdate");
        }

        /// <summary>The default welcome's greeting, as HTML with the member's name linked to their profile.</summary>
        private static string DefaultWelcome(XDocument lang, Message message) =>
            Methods.GetLocaleStringNoFormat(lang, "defaultWelcome", WelcomeText.Mention(message.NewChatMember),
                message.Chat.Title.FormatHTML());

        /// <summary>
        /// A custom welcome with its GIF, the text as an HTML caption. If the admin's HTML does not parse, the
        /// caption goes out unformatted with the plain name, as it always did.
        /// </summary>
        private static Message SendWelcomeGif(long chatId, string fileId, string html, string plain)
        {
            try
            {
                return Bot.Api.SendDocument(chatId, InputFile.FromFileId(fileId), html, ParseMode.Html).Result;
            }
            catch (Exception e) when (Bot.IsParseFailure(Bot.AsApiError(e)))
            {
                return Bot.Api.SendDocument(chatId, InputFile.FromFileId(fileId), plain).Result;
            }
        }

        public static void BotAdded(Message updateMessage)
        {
            var groupBan = Redis.db.HashGetAsync($"groupBan:{updateMessage.Chat.Id}", "banned").Result;
            var lang = Methods.GetGroupLanguage(updateMessage,true).Doc;
            if (groupBan.Equals("1"))
            {
               
                Bot.Send(Methods.GetLocaleString(lang, "groupBanned"), updateMessage.Chat.Id);
                Bot.Api.LeaveChat(updateMessage.Chat.Id);
                return;
            }
            var alreadyExists = Redis.db.SetContainsAsync($"bot:groupsid", updateMessage.Chat.Id).Result;
            if (alreadyExists)
            {
                Bot.Send(
                    Methods.GetLocaleString(lang, "welcomeBack"),
                    updateMessage.Chat.Id);
            }
            else
            {
                Bot.Send(
                    "Welcome to Enforcer. Default settings have been loaded, please use /menu to configure them or /help for more information",
                    updateMessage.Chat.Id);
                IntiliseSettings(updateMessage.Chat.Id);
            }
        }

        private static void IntiliseSettings(long chatId)
        {
            GenerateSettings(chatId);
            Redis.db.SetAddAsync($"bot:groupsid", chatId);
            Redis.db.SetAddAsync("bot:e5groupsid", chatId); 
        }

        public static void ResetUser(Message message)
        {
            var lang = Methods.GetGroupLanguage(message,true).Doc;
            Methods.UnbanUser(message.Chat.Id, message.NewChatMember.Id, lang);
        }   

        /// <summary>
        /// A new group's settings (the bot added, /resetSettings): the group's config (rollout step 15: its settings and
        /// welcome; texts and extras stay) and its detection settings.
        /// </summary>
        public static void GenerateSettings(long chatId)
        {
            GroupConfigs.TryChange(chatId, ConfigChange.Reset());
            // Flood, media, scripts, lengths and warns live in PostgreSQL (Detection step 2).
            Detection.DetectionSettings.TryUpdate(chatId, p => p.WithNewGroupDefaults());
        }


        /// <summary>
        /// Logs what the bot did on its own. <paramref name="line"/> is the HTML the bot told the group (a language
        /// string, its arguments escaped already); it goes into the log line's &lt;code&gt;, as plain text, since
        /// LogCommand escapes it again (names showed as A&amp;amp;B, issue #29).
        /// </summary>
        public static void LogBotAction(long chatId, string line, long? targetId = null)
        {
            var target = targetId.HasValue ? GetCachedUserName(targetId.Value) : "";
            LogCommand(chatId, -1, "Enforcer", GetCachedGroupTitle(chatId), PlainText(line), target, digestDuringRaid: true);
        }

        /// <summary>HTML as the reader sees it: tags dropped, entities decoded. Marked ids and mentions are kept.</summary>
        internal static string PlainText(string html) =>
            html == null ? null : System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", ""));

        // Callbacks whose data is "trigger:chatId:userId", so args[2] is the affected user.
        private static readonly HashSet<string> UserTargetCallbacks = new(StringComparer.OrdinalIgnoreCase)
        {
            "banflag", "kickflag", "warnflag", "resetwarns", "removewarn", "resetPrewarns", "removePrewarn",
            "userbuttonresetwarn", "userbuttonremwarns", "userbuttonbanuser", "userbuttonwarnuser",
            "usermediaremwarns", "usermediaresetwarn", Detection.DetectionReports.Trigger
        };

        /// <summary>
        /// Callbacks that write their own, readable log entry once they have done something, so the
        /// generic "pressed button X" entry would only duplicate it.
        /// </summary>
        private static readonly HashSet<string> SelfLoggingCallbacks = new(StringComparer.OrdinalIgnoreCase)
        {
            "resetwarns", "removewarn", "resetPrewarns", "removePrewarn",
            "userbuttonresetwarn", "userbuttonremwarns", "usermediaremwarns", "usermediaresetwarn",
            "warnreason", Recovery.RecoveryOffers.Trigger,
            // One line for the whole batch, at its end.
            Actions.Batches.Trigger
        };

        /// <summary>
        /// Logs an admin button press with the group name, the full callback data and, where the
        /// callback carries one, the affected user. Names come from the Redis caches CollectStats
        /// maintains, so no Telegram calls are made; the ID is the fallback.
        /// </summary>
        public static void LogCallback(long chatId, User admin, string callbackData, Message buttonMessage = null,
            (InlineKeyboardMarkup Keys, string Text) shown = default, InlineKeyboardMarkup redrawn = null)
        {
            var target = "";
            var parts = callbackData.Split(':');
            if (SelfLoggingCallbacks.Contains(parts[0])) return;
            // A menu button: what it changed, read off the menu ("changed Late edits: On → Off").
            if (!UserTargetCallbacks.Contains(parts[0]) && !ReportCallbacks.ContainsKey(parts[0])
                && MenuChangeLog.Log(chatId, admin, callbackData, shown.Keys, shown.Text, redrawn)) return;
            if (parts.Length >= 3 && UserTargetCallbacks.Contains(parts[0]) && long.TryParse(parts[2], out var targetId))
                target = GetCachedUserName(targetId);
            LogCommand(chatId, admin.Id, admin.FirstName, GetCachedGroupTitle(chatId), callbackData, target,
                linkMessageId: CallbackLinkMessageId(chatId, parts, buttonMessage));
        }

        // Report buttons, with the position of the report's message id in their callback data.
        private static readonly Dictionary<string, int> ReportCallbacks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["banflag"] = 3, ["kickflag"] = 3, ["warnflag"] = 3, ["delflag"] = 3, ["solveflag"] = 2
        };

        /// <summary>
        /// The group message a button press's log entry links to: the report, for the buttons on a
        /// report (which admins press in their PM); otherwise the message the button was on, if that
        /// is in the group. Null when there is nothing in the group to link to.
        /// </summary>
        internal static long? CallbackLinkMessageId(long chatId, string[] parts, Message buttonMessage)
        {
            if (ReportCallbacks.TryGetValue(parts[0], out var index) && parts.Length > index
                && long.TryParse(parts[index], out var reportId))
                return reportId;
            return buttonMessage != null && buttonMessage.Chat.Id == chatId ? buttonMessage.MessageId : null;
        }

        /// <summary>
        /// A "Go to message" button for a log channel post, or null if the message cannot be linked:
        /// only supergroups have message links (t.me/c/...), which work for any member.
        /// </summary>
        internal static InlineKeyboardMarkup LogLinkButton(XDocument lang, long chatId, long? messageId) =>
            LogLinkUrl(chatId, messageId) is { } url
                ? new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl(Methods.GetLocaleString(lang, "goToMessage"), url))
                : null;

        /// <summary>The link behind <see cref="LogLinkButton"/>, or null.</summary>
        internal static string LogLinkUrl(long chatId, long? messageId) =>
            messageId is not > 0 || !chatId.ToString().StartsWith("-100") ? null
                : Methods.GetChatMessageLink(chatId, messageId.Value.ToString());

        /// <summary>The group's title from the stats cache, unescaped; empty if unknown.</summary>
        internal static string GetCachedGroupTitle(long chatId)
        {
            try
            {
                var name = Redis.db.HashGetAsync($"chat:{chatId}:details", "name").Result;
                // LogCommand already appends "(chatId)", so an unknown name is just left blank.
                return name.IsNullOrEmpty ? "" : name.ToString();
            }
            catch (Exception ex)
            {
                ErrorReporter.Report(ex, $"Reading the cached title of {chatId}");
                return "";
            }
        }

        // Unescaped: it goes to LogCommand as a language argument, which escapes it.
        private static string GetCachedUserName(long userId) => ShownUserLabel(userId);

        /// <summary>
        /// <see cref="GetCachedUserLabel"/> to show: the name a mention, the id copyable (<see cref="Ids"/>). Unescaped,
        /// for a language argument. Never store it.
        /// </summary>
        internal static string ShownUserLabel(long userId) => GetCachedUserLabel(userId, Ids.Mark, Ids.Mention);

        /// <summary>
        /// "Name @username (id)" from the stats cache, unescaped, for passing to GetLocaleString
        /// (which escapes its arguments). Just the id if the user is not cached.
        /// </summary>
        internal static string GetCachedUserLabel(long userId) => GetCachedUserLabel(userId, id => id.ToString(), (_, label) => label);

        private static string GetCachedUserLabel(long userId, Func<long, string> showId, Func<long, string, string> showName)
        {
            try
            {
                var fields = Redis.db.HashGetAsync($"user:{userId}", new RedisValue[] { "name", "username" }).Result;
                var name = fields[0].IsNullOrEmpty ? null : fields[0].ToString();
                var username = fields[1].IsNullOrEmpty ? null : fields[1].ToString();
                var label = string.Join(" ", new[] { name, username }.Where(s => s != null));
                return string.IsNullOrEmpty(label) ? showId(userId) : $"{showName(userId, label)} ({showId(userId)})";
            }
            catch (Exception ex)
            {
                ErrorReporter.Report(ex, $"Reading the cached name of {userId}");
                return showId(userId);
            }
        }

        // Commands already logged: one line per command, whether the command logged itself or the actions it ran
        // did (Actions/ActionLog), and however many actions it ran.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Message, object> LoggedCommands = new();

        /// <summary>
        /// Logs <paramref name="update"/>'s command once it acted on <paramref name="targetId"/> in
        /// <paramref name="chatId"/> (an action's line, <c>Actions/ActionLog</c>). In the group itself, exactly as
        /// <see cref="LogCommand(Update, string)"/>; from a private chat, in the group's log channel with the
        /// member named.
        /// </summary>
        internal static void LogCommand(long chatId, long targetId, Update update)
        {
            var message = update.Message;
            if (message.Chat.Id == chatId)
            {
                LogCommand(update, message.Text);
                return;
            }
            if (!FirstLog(message)) return;
            LogCommand(chatId, message.From.Id, message.From.FirstName, GetCachedGroupTitle(chatId), message.Text,
                targetId > 0 ? ArgTargetName(targetId) : "");
        }

        private static bool FirstLog(Message message)
        {
            lock (LoggedCommands)
            {
                if (LoggedCommands.TryGetValue(message, out _)) return false;
                LoggedCommands.Add(message, null);
                return true;
            }
        }

        public static void LogCommand(Update update, string command)
        {          
                if (!FirstLog(update.Message)) return;
                var adminUserId = update.Message.From.Id;
                var adminUserName = update.Message.From.FirstName;
                var groupName = update.Message.Chat.Title;
                var messageId = update.Message.MessageId;
                if(update.Message.ReplyToMessage != null)
                    LogCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command, Ids.Named(update.Message.ReplyToMessage.From.Id, update.Message.ReplyToMessage.From.FirstName), linkMessageId: messageId);
                else if (Methods.ArgTarget(update) is long target)
                    // Named by id, mention or @username: log the id too, as a reply does.
                    LogCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command, ArgTargetName(target), linkMessageId: messageId);
                else
                {
                    LogCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command, linkMessageId: messageId);
                }                                           
        }

        /// <summary>
        /// "Name (id)" for a member a command named by argument, the name a mention; the id alone when no name is
        /// stored. Unescaped, for a language argument.
        /// </summary>
        internal static string ArgTargetName(long userId)
        {
            string name = null;
            try
            {
                name = Redis.db.HashGetAsync($"user:{userId}", "name").Result;
            }
            catch (Exception)
            {
                // A missing name only costs the name: the id is what the log needs.
            }
            return string.IsNullOrWhiteSpace(name) ? Ids.Mark(userId) : Ids.Named(userId, name);
        }

         public static void LogDevCommand(Update update, string command)
        {          
                long adminUserId = update.Message.From.Id;
                var adminUserName = update.Message.From.FirstName;
                var groupName = update.Message.Chat.Title;
                if(update.Message.ReplyToMessage != null)
                    LogDevCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command, Ids.Named(update.Message.ReplyToMessage.From.Id, update.Message.ReplyToMessage.From.FirstName));
                else
                {
                    LogDevCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command);
                }                                           
        }


        public static void LogCommand(long chatId, long adminId, string adminName, string groupname, string command, string replyto = "", bool isCallback = false,
            long? linkMessageId = null, bool digestDuringRaid = false)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (string.IsNullOrEmpty(replyto))
            {              
                replyto = Methods.GetLocaleString(lang, "noone");
            }

            if (!isCallback)
            {
                SendToLogChannel(chatId, lang, () => Methods.GetLocaleString(lang, "logMessageCommand", ShownName(adminId, adminName), ShownActor(adminId), command, $"{groupname} ({Ids.Mark(chatId)})", replyto), linkMessageId,
                    digestDuringRaid);
            }
            else
            {
                SendToLogChannel(chatId, lang, () => Methods.GetLocaleString(lang, "logMessageCallback", ShownName(adminId, adminName), ShownActor(adminId), command, Ids.Mark(chatId)), linkMessageId);
            }
        }

        // The bot's own lines are "Enforcer (-1)": that is no id to copy, and no one to mention.
        private static string ShownActor(long adminId) => adminId > 0 ? Ids.Mark(adminId) : adminId.ToString();

        // Who acted, unescaped (a language argument): a mention, unless it is the bot.
        private static string ShownName(long adminId, string adminName) => adminId > 0 ? Ids.Mention(adminId, adminName) : adminName;

        /// <summary>
        /// Posts <paramref name="text"/> (HTML) to the group's log channel, if it has one. If the bot
        /// has lost access to the channel, the setting is removed and the group told. The text is
        /// only built when there is a channel, and a failure building it is reported like a failed
        /// send, as before this was split out of LogCommand. With <paramref name="linkMessageId"/>
        /// the post gets a button to that message in the group.
        /// </summary>
        internal static void SendToLogChannel(long chatId, XDocument lang, Func<string> text, long? linkMessageId = null,
            bool digestDuringRaid = false)
        {
            if (LogChannelOf(chatId) is { } logChat)
            {
                try
                {
                    // The bot's own actions during a raid: one post every LogDigest.Every with all of
                    // them, not one per raider. What admins do is still posted at once.
                    if (digestDuringRaid && Raids.RaidGuard.Quiet(chatId))
                    {
                        LogDigest.Add(chatId, logChat, lang, text());
                        return;
                    }
                    // Posted in the background (LogQueue), which also handles a channel the bot lost.
                    LogQueue.Enqueue(chatId, logChat, text(), LogLinkUrl(chatId, linkMessageId),
                        Methods.GetLocaleString(lang, "goToMessage"));
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Posting to the log channel of {chatId}");
                }
            }
        }

        /// <summary>The group's log channel; null when it has none.</summary>
        internal static long? LogChannelOf(long chatId)
        {
            // In the group's config (in memory) since rollout step 15; it was two Redis reads per log line.
            return GroupConfigs.For(chatId).LogChat;
        }

        public static void LogDevCommand(long chatId, long adminId, string adminName, string groupname, string command, string replyto = "")
        {
            var lang = Methods.GetGroupLanguage(Bot.ErrorChatId).Doc;
            if (string.IsNullOrEmpty(replyto))
            {
                replyto = Methods.GetLocaleString(lang, "noone");
            }
              
                try
                {

                    if (groupname != null)
                    {
                        LogQueue.Enqueue(0, -1001141798933,
                            Methods.GetLocaleString(lang, "logMessageCommand", ShownName(adminId, adminName), ShownActor(adminId), command, $"{groupname} ({Ids.Mark(chatId)})", replyto));
                    }
                    else
                    {
                        LogQueue.Enqueue(0, -1001141798933,
                            Methods.GetLocaleString(lang, "logMessageCallback", ShownName(adminId, adminName), ShownActor(adminId), command, Ids.Mark(chatId)));
                    }

                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, "Posting to the dev log channel");
                    Bot.Send(Methods.GetLocaleString(lang, "logSendError"), chatId);
                }
            }



    }
}
