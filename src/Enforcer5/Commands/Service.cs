using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    public static class Service
    {
        public static async Task Welcome(Message message)
        {
            var msgs = Redis.db.StringGetAsync($"spam:added:{message.Chat.Id}").Result;
            var defSpamValue = 3;
            var maxTime = TimeSpan.FromSeconds(30);
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
            await Redis.db.StringSetAsync($"spam:added:{message.Chat.Id}", msg + 1, maxTime);
            if (msg >= defSpamValue+1)
            {
               return; 
            }
            var joinSpam = Redis.db.StringGetAsync($"spam:added:{message.Chat.Id}:{message.NewChatMember.Id}").Result;
            defSpamValue = 3;
            maxTime = TimeSpan.FromMinutes(5);
            if (msgs.HasValue && !string.IsNullOrEmpty(msgs))
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
            await Redis.db.StringSetAsync($"spam:added:{message.Chat.Id}:{message.NewChatMember.Id}", msg + 1, maxTime);
            if (msg >= defSpamValue + 1)
            {
                return;
            }
            joinSpam = Redis.db.StringGetAsync($"spam:added:{message.NewChatMember.Id}").Result;
            defSpamValue = 3;
            maxTime = TimeSpan.FromMinutes(5);
            if (msgs.HasValue && !string.IsNullOrEmpty(msgs))
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
            await Redis.db.StringSetAsync($"spam:added:{message.NewChatMember.Id}", msg + 1, maxTime);
            if (msg >= defSpamValue + 1)
            {
                return;
            }
            var type = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "type").Result;
            var content = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "content").Result;
            if (!string.IsNullOrEmpty(type) && type.Equals("media"))
            {
                var file_id = content;
                await Bot.Api.SendDocumentAsync(message.Chat.Id, file_id);
            }
            else if (!string.IsNullOrEmpty(type) && type.Equals("custom"))
            {
                var hasMedia = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "hasmedia").Result;
                if (!string.IsNullOrEmpty(hasMedia) && hasMedia.Equals("true"))
                {
                    var file = Redis.db.HashGetAsync($"chat:{message.Chat.Id}:welcome", "media").Result;
                    var text = GetCustomWelcome(message, content);
                    await Bot.Api.SendDocumentAsync(message.Chat.Id, file, text);
                }
                else
                {
                    var text = GetCustomWelcome(message, content);
                    await Bot.Send(text, message.Chat.Id);
                }
            }
            else if (!string.IsNullOrEmpty(type) && type.Equals("composed"))
            {
                var lang = Methods.GetGroupLanguage(message).Doc;
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
                    await Bot.Api.SendTextMessageAsync(message.Chat.Id, text);
                }
                else
                {
                    var text = Methods.GetLocaleString(lang, "defaultWelcome", message.NewChatMember.FirstName,
                        message.Chat.Title);
                    await Bot.Api.SendTextMessageAsync(message.Chat.Id, text);
                }
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

        public static async Task BotAdded(Message updateMessage)
        {
            var groupBan = Redis.db.HashGetAsync($"groupBan:{updateMessage.Chat.Id}", "banned").Result;
            if (groupBan.Equals("1"))
            {
                var lang = Methods.GetGroupLanguage(updateMessage).Doc;
                await Bot.Send(Methods.GetLocaleString(lang, "groupBanned"), updateMessage.Chat.Id);
                await Bot.Api.LeaveChatAsync(updateMessage.Chat.Id);
                return;
            }
            var alreadyExists = Redis.db.SetContainsAsync($"bot:groupsid", updateMessage.Chat.Id).Result;
            if (alreadyExists)
            {
                await Bot.Send(
                    "Welcome back to Enforcer. Your settings are still intack",
                    updateMessage.Chat.Id);
            }
            else
            {
                await Bot.Send(
                    "Welcome to Enforcer. Default settings have been loaded, please use /menu to configure them or /help for more information",
                    updateMessage.Chat.Id);
                IntiliseSettings(updateMessage.Chat.Id);
            }
        }

        private static void IntiliseSettings(long chatId)
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
            Redis.db.SetAddAsync($"bot:groupsid", chatId);
            Redis.db.SetAddAsync("bot:e5groupsid", chatId); 
        }

        public static async Task ResetUser(Message message)
        {
            var lang = Methods.GetGroupLanguage(message).Doc;
            Methods.UnbanUser(message.Chat.Id, message.NewChatMember.Id, lang);
        }
    }
}
