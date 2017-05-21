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
                case 352277339://Phyto
                    Bot.Send("Error 404", chatId);
                    break;
                case 106665913://Jeff
                    Bot.Send("This is a known bug. No need to report", chatId);
                    break;
                case 23776848://Melisa
                    Bot.Send("Banhammer is ready for use milady. Feel free to strike them down", chatId);
                    break;
                case 9375804://Jhen
                    Bot.Send("Or else I'll kick your butt", chatId);
                    break;
                case 125311351://Daniel
                    Bot.Send("401 Not authorised", chatId);
                    break;
                case 223494929:
#if premium
                    Bot.Api.SendDocumentAsync(message.Chat.Id, "CgADBAADOCEAAhsXZAfnHwfv4ufK6wI");
#endif
#if normal
                    Bot.Api.SendDocumentAsync(message.Chat.Id, "CgADBAADOCEAAhsXZAePhP7wDwUKmgI");
#endif
                    break;
                case 263451571://Michelle
                    Bot.Send("The Node Queen is here! This Vixen is ready to slay.", chatId);
                    break;
                case 295152997://Ludwig
                    Bot.Send("Ludwig has joined the group. 1 crazy ape, 1 minimum, 1 max.", chatId);
                    break;
                case 81772130://Lordy
                    Bot.Send("Your a bad admin. Be a good admin - Budi", chatId);
                    break;
                case 221962247://Touka
                    Bot.Send("Uhm, who are you again? I may not remember for sure, but feel free to spread terror with your kagune here.", chatId);
                    break;
                default:
                    var type = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "type").Result;
                    var content = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "content").Result;
                    if (!string.IsNullOrEmpty(type) && type.Equals("media"))
                    {
                        var file_id = content;
                        Bot.Api.SendDocumentAsync(message.Chat.Id, file_id);
                    }
                    else if (!string.IsNullOrEmpty(type) && type.Equals("custom"))
                    {
                        var hasMedia = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "hasmedia").Result;
                        if (!string.IsNullOrEmpty(hasMedia) && hasMedia.Equals("true"))
                        {
                            var file = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "media").Result;
                            var text = GetCustomWelcome(message, content);
                            Bot.Api.SendDocumentAsync(message.Chat.Id, file, text);
                        }
                        else
                        {
                            var text = GetCustomWelcome(message, content);
                            Bot.Send(text, message.Chat.Id);
                        }
                    }
                    else if (!string.IsNullOrEmpty(type) && type.Equals("composed"))
                    {
                        XDocument lang;
                        try
                        {
                            lang = Methods.GetGroupLanguage(message).Doc;
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
                            Bot.Api.SendTextMessageAsync(message.Chat.Id, text);
                        }
                        else
                        {
                            var text = Methods.GetLocaleString(lang, "defaultWelcome", message.NewChatMember.FirstName,
                                message.Chat.Title);
                            Bot.Api.SendTextMessageAsync(message.Chat.Id, text);
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
            string welcome = content.ToString().Replace("$name", name);
            welcome = welcome.Replace("$username", username);
            welcome = welcome.Replace("$id", id.ToString());
            welcome = welcome.Replace("$title", message.Chat.Title);
            return welcome;
        }

        public static void BotAdded(Message updateMessage)
        {
            var groupBan = Redis.db.HashGetAsync($"groupBan:{updateMessage.Chat.Id}", "banned").Result;
            var lang = Methods.GetGroupLanguage(updateMessage).Doc;
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
            var lang = Methods.GetGroupLanguage(message).Doc;
            Methods.UnbanUser(message.Chat.Id, message.NewChatMember.Id, lang);
        }   

        public static void NewSettings(long chatid)
        {
            Redis.db.HashSetAsync($"chat:{chatid}:nsfwDetection", "activated", "on");
            Redis.db.HashSetAsync($"chat:{chatid}:nsfwDetection", "action", "ban");
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
                    {"antitextlengthsettings", "enabled", "yes" },
                    {"antitextlengthsettings", "maxlength", 1024 },
                    {"antitextlengthsettings", "maxlines", 50 },
                    {"antitextlengthsettings", "action", "kick" },

                    {"antinamelengthsettings", "enabled", "yes" },
                    {"antinamelengthsettings", "maxlength", 50 },
                    {"antinamelengthsettings", "action", "kick" },
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
                var adminUserId = update.Message.From.Id;
                var adminUserName = update.Message.From.FirstName;
                var groupName = update.Message.Chat.Title;
                if(update.Message.ReplyToMessage != null)
                    LogDevCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command, $"{update.Message.ReplyToMessage.From.FirstName} ({update.Message.ReplyToMessage.From.Id})");
                else
                {
                    LogDevCommand(update.Message.Chat.Id, adminUserId, adminUserName, groupName, command);
                }                                           
        }


        public static void LogCommand(long chatId, int adminId, string adminName, string groupname, string command, string replyto = "")
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

                    if (groupname != null)
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
                    Bot.Send(Methods.GetLocaleString(lang, "logSendError"), chatId);
                }
            }


        }

        public static void LogDevCommand(long chatId, int adminId, string adminName, string groupname, string command, string replyto = "")
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


       
    }
}
