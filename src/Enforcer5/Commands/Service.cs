﻿using System;
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
            var type = Redis.db.HashGet($"chat:{message.Chat.Id}:welcome", "type");
            var content = Redis.db.HashGet($"chat:{message.Chat.Id}:welcome", "content");
            if (!string.IsNullOrEmpty(type) && type.Equals("media"))
            {
                var file_id = content;
                await Bot.Api.SendDocumentAsync(message.Chat.Id, file_id);
            }
            else if (!string.IsNullOrEmpty(type) && type.Equals("custom"))
            {
                var hasMedia = Redis.db.HashGet($"chat:{message.Chat.Id}:welcome", "hasMedia");
                if (!string.IsNullOrEmpty(hasMedia) && hasMedia.Equals("true"))
                {
                    var file = Redis.db.HashGet($"chat:{message.Chat.Id}:welcome", "media");
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
            return "Still being implemented";//TO-DO Implement custom welcome
        }

        public static async Task BotAdded(Message updateMessage)
        {
            var groupBan = Redis.db.HashGet($"groupBan:{updateMessage.Chat.Id}", "banned");
            if (groupBan.Equals("1"))
            {
                var lang = Methods.GetGroupLanguage(updateMessage).Doc;
                await Bot.Send(Methods.GetLocaleString(lang, "groupBanned"), updateMessage.Chat.Id);
                await Bot.Api.LeaveChatAsync(updateMessage.Chat.Id);
                return;
            }
            var alreadyExists = Redis.db.SetContains($"bot:groupsid", updateMessage.Chat.Id);
            if (!alreadyExists)
            {

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
                    {"settings", "Rules", "no"},
                    {"settings", "About", "no"},
                    {"settings", "Modlist", "no"},
                    {"settings", "Report", "yes"},
                    {"settings", "Welcome", "yes"},
                    {"settings", "Extra", "yes"},
                    {"settings", "Flood", "no"},
                    {"settings", "Admin_mode", "yes"},
                    {"flood", "MaxFlood", 5},
                    {"flood", "ActionFlood", "kick"},
                    {"char", "Arab", "allowed"},
                    {"char", "Rtl", "allowed"},
                    {"floodexceptions", "image", "no"},
                    {"floodexceptions", "video", "no"},
                    {"floodexceptions", "sticker", "no"},
                    {"floodexceptions", "gif", "no"},
                    {"warnsettings", "type", "ban"},
                    {"warnsettings", "max", 3},
                    {"warnsettings", "mediamax", 2},
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
                        Redis.db.HashSet(hash, defaultSettings[i, j, 1].ToString(), num);
                    }
                    else if (setting is string)
                    {
                        Redis.db.HashSet(hash, defaultSettings[i, j, 1].ToString(), defaultSettings[i, j, 2].ToString());
                    }
                }
            }
            Redis.db.SetAdd($"bot:groupsid", chatId);
        }
    }
}