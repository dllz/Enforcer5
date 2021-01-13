using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection.Emit;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Helpers;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
#pragma warning disable CS4014
namespace Enforcer5
{ 
    public static class Service
    {
        public static void Welcome(Message message)
        {
            var chatId = message.Chat.Id;
            var welcomeOn = Redis.db.HashGetAsync($"chat:{chatId}:settings", "Welcome").Result;
            if (welcomeOn.Equals("yes"))
                return;
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
                    Console.WriteLine(e);
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
                    Console.WriteLine(e);
                    
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
                    Console.WriteLine(e);

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
                case 106665913://Jeff
                    Bot.Send("This is a known bug. No need to report", chatId);
                    break;
                case 125311351://Daniel
#if premium
                    Bot.Api.SendDocumentAsync(message.Chat.Id, new FileToSend("CgADBAADZCgAApwaZAfDe5MFy-IHCAI"));
#endif
#if normal
                    Bot.Api.SendDocumentAsync(message.Chat.Id, new FileToSend("CgADBAADZCgAApwaZAfmRkSuLkIV9AI"));
#endif
                    break;
                case 223494929:
#if premium
                    Bot.Api.SendDocumentAsync(message.Chat.Id, new FileToSend("CgADBAADOCEAAhsXZAfnHwfv4ufK6wI"));
#endif
#if normal
                    Bot.Api.SendDocumentAsync(message.Chat.Id, new FileToSend("CgADBAADOCEAAhsXZAePhP7wDwUKmgI"));
#endif
                    break;
                case 295152997://Ludwig
                    Bot.Send("Everyone beware, a crazy ape is about to infiltrate this group!", chatId);
                    break;
                case 81772130://Lordy
                    Bot.Send("You're a bad admin. Be a good admin - Budi", chatId);
                    break;
                case 36702373://Karma (Prize Winner)
                    Bot.Send("Let the players play, let the haters hate. And I, Karma, will handle their fate.\nRemember: Karma's gonna come collect your debt.", chatId);
                    break;
                default:
                    var type = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "type").Result;
                    var content = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "content").Result;
                    if (!string.IsNullOrEmpty(type) && type.Equals("media"))
                    {
                        var file_id = content;
                        Message response = Bot.Api.SendDocumentAsync(message.Chat.Id, new FileToSend(file_id)).Result;

                        Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                    }
                    else if (!string.IsNullOrEmpty(type) && type.Equals("custom"))
                    {
                        var hasMedia = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "hasmedia").Result;
                        if (!string.IsNullOrEmpty(hasMedia) && hasMedia.Equals("true"))
                        {
                            var file = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "media").Result;
                            var text = GetCustomWelcome(message, content);
                            Message response = Bot.Api.SendDocumentAsync(message.Chat.Id, new FileToSend(file), text).Result;

                            Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                        }
                        else
                        {
                            var text = GetCustomWelcome(message, content);
                            Message response = Bot.Send(text, message.Chat.Id);

                            Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                        }
                    }
                    else if (!string.IsNullOrEmpty(type) && type.Equals("composed"))
                    {
                        XDocument lang;
                        try
                        {
                            lang = Methods.GetGroupLanguage(message,true).Doc;
                        }
                        catch (NullReferenceException e)
                        {
                            try
                            {
                                lang = Methods.GetGroupLanguage(-1001076212715).Doc;
                            }
                            catch (NullReferenceException exception)
                            {
                                Console.WriteLine(exception);
                                return;
                            }
                        }
                        if (!content.Equals("no"))
                        {
                            var text = Methods.GetLocaleString(lang, "defaultWelcome", message.NewChatMember.FirstName,
                                message.Chat.Title);
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
                            Message response = Bot.Api.SendTextMessageAsync(message.Chat.Id, text).Result;

                            Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                        }
                        else
                        {
                            var text = Methods.GetLocaleString(lang, "defaultWelcome", message.NewChatMember.FirstName,
                                message.Chat.Title);
                            Message response = Bot.Api.SendTextMessageAsync(message.Chat.Id, text).Result;

                            Bot.DeleteLastWelcomeMessage(message.Chat.Id, response.MessageId);
                        }
                    }
                    break;
            }            
        }

        private static string GetCustomWelcome(Message message, RedisValue content)
        {
            var name = message.NewChatMember.FirstName;
            var id = message.NewChatMember.Id;
            string username;
            if (!string.IsNullOrEmpty(message.NewChatMember.Username))
            {
                username = $"@{message.NewChatMember.Username}";
            }
            else
            {
                username = "(no username)";
            }
            string welcome = content.ToString().Replace("$name", Methods.FormatHTML(name));
            welcome = welcome.Replace("$username", username);
            welcome = welcome.Replace("$id", id.ToString());
            welcome = welcome.Replace("$title", Methods.FormatHTML(message.Chat.Title));
            return welcome;
        }

        public static void BotAdded(Message updateMessage)
        {
            var groupBan = Redis.db.HashGetAsync($"groupBan:{updateMessage.Chat.Id}", "banned").Result;
            var lang = Methods.GetGroupLanguage(updateMessage,true).Doc;
            if (groupBan.Equals("1"))
            {
               
                Bot.Send(Methods.GetLocaleString(lang, "groupBanned"), updateMessage.Chat.Id);
                Bot.Api.LeaveChatAsync(updateMessage.Chat.Id);
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

        public static void NewSettings(long chatid)
        {
            Redis.db.HashSetAsync($"chat:{chatid}:nsfwDetection", "activated", "on");
            Redis.db.HashSetAsync($"chat:{chatid}:nsfwDetection", "action", "ban");
            
        }

        public static void NewSetting2(long chatid)
        {
           
            object[,,] defaultSettings =
            {
                {
                    { "media", "image", "allowed"},
                    { "media", "audio", "allowed"},
                    { "media", "video", "allowed"},
                    { "media", "sticker", "allowed"},
                    { "media", "gif", "allowed"},
                    { "media", "voice", "allowed"},
                    { "media", "contact", "allowed"},
                    { "media", "file", "allowed"},
                    { "media", "link", "allowed"},
                    {"media", "action", "tempban" }
                }
            };


            var num = 0;
            for (int i = 0; i < defaultSettings.GetLength(0); i++)
            {
                for (int j = 0; j < defaultSettings.GetLength(1); j++)
                {
                    var hash = $"chat:{chatid}:{defaultSettings[i, j, 0]}";
                    var value = defaultSettings[i, j, 1];
                    var setting = defaultSettings[i, j, 2];
                    if (int.TryParse(setting.ToString(), out num))
                    {
                        Redis.db.HashSetAsync(hash, defaultSettings[i, j, 1].ToString(), num);
                    }
                    else if (setting is string)
                    {
                        Redis.db.HashSetAsync(hash, defaultSettings[i, j, 1].ToString(), defaultSettings[i, j, 2].ToString());
                    }
                }
            }
        }

        public static void removeWarn0(long chatid, long userId)
        {
            var currentMedia = Convert.ToInt32(Redis.db.HashGetAsync($"chat:{chatid}:mediawarn", userId).Result);
            var currentWarn = Convert.ToInt32(Redis.db.HashGetAsync($"chat:{chatid}:warns", userId).Result);

            if (currentWarn < 0)
            {
                Redis.db.HashSetAsync($"chat:{chatid}:warns", userId, 0);

            }
            if(currentMedia < 0)
                Redis.db.HashSetAsync($"chat:{chatid}:mediawarn", userId, 0);
        }

        public static void GenerateSettings(long chatId)
        {
            object[,,] defaultSettings =
            {
                {
                    {"settings", "Rules", "yes"},
                    {"settings", "About", "yes"},
                    {"settings", "Modlist", "yes"},
                    {"settings", "Report", "yes"},
                    {"settings", "Welcome", "yes"},
                    {"settings", "Extra", "yes"},
                    {"settings", "Flood", "yes"},
                    {"settings", "Admin_mode", "no"},
                    {"flood", "MaxFlood", 5},
                    {"flood", "ActionFlood", "kick"},
                    {"char", "Arab", "allowed"},
                    {"char", "Rtl", "allowed"},
                    {"floodexceptions", "image", "yes"},
                    {"floodexceptions", "video", "yes"},
                    {"floodexceptions", "sticker", "yes"},
                    {"floodexceptions", "gif", "yes"},
                    {"warnsettings", "type", "ban"},
                    {"warnsettings", "max", 5},
                    {"warnsettings", "mediamax", 3},
                    {"welcome", "type", "composed"},
                    {"welcome", "content", "no"},
                    {"media", "image", "allowed"},
                    {"media", "audio", "allowed"},
                    {"media", "video", "allowed"},
                    {"media", "sticker", "allowed"},
                    {"media", "gif", "allowed"},
                    {"media", "voice", "allowed"},
                    {"media", "contact", "allowed"},
                    {"media", "file", "allowed"},
                    {"media", "link", "allowed"},
                    {"media", "action", "tempban" },
                    {"antitextlengthsettings", "enabled", "yes" },
                    {"antitextlengthsettings", "maxlength", 1024 },
                    {"antitextlengthsettings", "maxlines", 50 },
                    {"antitextlengthsettings", "action", "kick" },

                    {"antinamelengthsettings", "enabled", "yes" },
                    {"antinamelengthsettings", "maxlength", 50 },
                    {"antinamelengthsettings", "action", "kick" },
                    {"nsfwDetection", "activated", "on"},
                    {"nsfwDetection", "action", "ban" },

                }
            };


            var num = 0;
            for (int i = 0; i < defaultSettings.GetLength(0); i++)
            {
                for (int j = 0; j < defaultSettings.GetLength(1); j++)
                {
                    var hash = $"chat:{chatId}:{defaultSettings[i, j, 0]}";
                    var value = defaultSettings[i, j, 1];
                    var setting = defaultSettings[i, j, 2];
                    if (int.TryParse(setting.ToString(), out num))
                    {
                        Redis.db.HashSetAsync(hash, defaultSettings[i, j, 1].ToString(), num);
                    }
                    else if (setting is string)
                    {
                        Redis.db.HashSetAsync(hash, defaultSettings[i, j, 1].ToString(), defaultSettings[i, j, 2].ToString());
                    }
                }
            }
        }

        public static void LogBotAction(long chatId, string command)
        {
            LogCommand(chatId, -1, "Enforcer", Bot.Api.GetChatAsync(chatId).Result.Title, command);
        }

        public static void LogCommand(Update update, string command)
        {          
                var adminUserId = update.Message.From.Id;
                var adminUserName = update.Message.From.FirstName;
                var groupName = update.Message.Chat.Title;
                if(update.Message.ReplyToMessage != null)
                    LogCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command, $"{update.Message.ReplyToMessage.From.FirstName} ({update.Message.ReplyToMessage.From.Id})");
                else
                {
                    LogCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command);
                }                                           
        }

         public static void LogDevCommand(Update update, string command)
        {          
                long adminUserId = update.Message.From.Id;
                var adminUserName = update.Message.From.FirstName;
                var groupName = update.Message.Chat.Title;
                if(update.Message.ReplyToMessage != null)
                    LogDevCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command, $"{update.Message.ReplyToMessage.From.FirstName} ({update.Message.ReplyToMessage.From.Id})");
                else
                {
                    LogDevCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command);
                }                                           
        }


        public static void LogCommand(long chatId, long adminId, string adminName, string groupname, string command, string replyto = "", bool isCallback = false)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (string.IsNullOrEmpty(replyto))
            {              
                replyto = Methods.GetLocaleString(lang, "noone");
            }

            if (Redis.db.SetContainsAsync("logChatGroups", chatId).Result)
            {
                var logChatID = Redis.db.HashGetAsync($"chat:{chatId}:settings", "logchat").Result.ToString();              
                try
                {

                    if (!isCallback)
                    {
                        Bot.Send(Methods.GetLocaleString(lang, "logMessageCommand", adminName, adminId, command, $"{groupname} ({chatId})", replyto),
                            long.Parse(logChatID));
                    }                    
                    else
                    {
                        Bot.Send(Methods.GetLocaleString(lang, "logMessageCallback", adminName, adminId, command, $"{chatId}"),
                            long.Parse(logChatID));
                    }
               
                }
                catch (Exception e)
                {
                    List<string> removeTriggers = new List<string> { "bot was kicked from the", "bot is not a member of the", "bot was blocked by" };
                    if (e is AggregateException && ((AggregateException)e).InnerExceptions.Any(x => removeTriggers.Any(y => x.Message.Contains(y))))
                    {
                        Redis.db.HashDeleteAsync($"chat:{chatId}:settings", "logchat");
                        Bot.Send(Methods.GetLocaleString(lang, "logchannelAutoRemoved"), chatId);
                    }
                    else Bot.Send(Methods.GetLocaleString(lang, "logSendError"), chatId);
                }
            }


        }

        public static void LogDevCommand(long chatId, long adminId, string adminName, string groupname, string command, string replyto = "")
        {
            var lang = Methods.GetGroupLanguage(-1001076212715).Doc;
            if (string.IsNullOrEmpty(replyto))
            {
                replyto = Methods.GetLocaleString(lang, "noone");
            }
              
                try
                {

                    if (groupname != null)
                    {
                        Bot.Send(Methods.GetLocaleString(lang, "logMessageCommand", adminName, adminId, command, $"{groupname} ({chatId})", replyto),
                            -1001141798933);
                    }
                    else
                    {
                        Bot.Send(Methods.GetLocaleString(lang, "logMessageCallback", adminName, adminId, command, $"{chatId}"),
                           -1001141798933);
                    }

                }
                catch (Exception e)
                {
                    Bot.Send(Methods.GetLocaleString(lang, "logSendError"), chatId);
                }
            }


        public static void NewSetting3(ChatId chatid)
        {
            object[,,] defaultSettings =
            {
                {
                    { "settings", "Help", "yes"},                   
                }
            };


            var num = 0;
            for (int i = 0; i < defaultSettings.GetLength(0); i++)
            {
                for (int j = 0; j < defaultSettings.GetLength(1); j++)
                {
                    var hash = $"chat:{chatid}:{defaultSettings[i, j, 0]}";
                    var value = defaultSettings[i, j, 1];
                    var setting = defaultSettings[i, j, 2];
                    if (int.TryParse(setting.ToString(), out num))
                    {
                        Redis.db.HashSetAsync(hash, defaultSettings[i, j, 1].ToString(), num);
                    }
                    else if (setting is string)
                    {
                        Redis.db.HashSetAsync(hash, defaultSettings[i, j, 1].ToString(), defaultSettings[i, j, 2].ToString());
                    }
                }
            }
        }
    }
}
