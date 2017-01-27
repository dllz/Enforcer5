﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Enforcer5.Languages;
using Enforcer5.Models;
using StackExchange.Redis;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    public class Methods
    {
        public static async Task<bool> KickUser(long chatId, int userId, XDocument doc)
        {

            try
            {
                var res = Bot.Api.KickChatMemberAsync(chatId, userId, CancellationToken.None).Result;
                if (res)
                {
                    await Redis.db.HashIncrementAsync("bot:general", "kick", 1); //Save the number of kicks made by the bot
                    var check = await Bot.Api.GetChatMemberAsync(chatId, userId);
                    var status = check.Status;
                    var count = 0;

                    while (status == ChatMemberStatus.Member && count < 10)
                    {
                        check = await Bot.Api.GetChatMemberAsync(chatId, userId);
                        status = check.Status;
                        count++;
                    }
                    count = 0;
                    while (status != ChatMemberStatus.Left && count < 10)
                    {
                        await Bot.Api.UnbanChatMemberAsync(chatId, userId);
                        check = await Bot.Api.GetChatMemberAsync(chatId, userId);
                        status = check.Status;
                        count++;
                    }
                }
                return res;
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions[0].Message.Equals("Bad Request: Not enough rights to kick/unban chat member"))
                {
                    await Bot.Send(GetLocaleString(doc, "botNotAdmin"), chatId);
                    return false;
                }
                Methods.SendError(e.InnerExceptions[0], chatId, doc);
                return false;
            }

        }

        public static async void SendError(string exception, Message updateMessage, XDocument doc)
        {
            try
            {
                await Bot.SendReply(GetLocaleString(doc, "Error", exception), updateMessage);
            }
            catch (Exception e)
            {
                //fucked
            }
        }

        public static async void SendError(Exception exceptionInnerException, long chatid, XDocument doc)
        {
            object[] arguments =
                {
                    exceptionInnerException.Message
                };
            await Bot.Send(GetLocaleString(doc, "Error", arguments), chatid);
        }

        public static Language GetGroupLanguage(Message uMessage)
        {
            var lang = Redis.db.StringGetAsync($"chat:{uMessage.Chat.Id}:language").Result;
            if (lang.HasValue)
            {
                return Program.LangaugeList.FirstOrDefault(x => x.Name == lang);
            }
            else
            {
                return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
            }
        }
        public static Language GetGroupLanguage(long chatId)
        {
            var lang = Redis.db.StringGetAsync($"chat:{chatId}:language").Result;
            if (lang.HasValue)
            {
                return Program.LangaugeList.FirstOrDefault(x => x.Name == lang);
            }
            else
            {
                return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
            }
        }

        public static void IntialiseLanguages()
        {
            foreach (var language in Directory.GetFiles(Bot.LanguageDirectory, "*.xml"))
            {

                Program.LangaugeList.Add(new Language(language));
            }
        }

        /// Gets the matching language string and formats it with parameters
        /// </summary>
        /// <param name="file">The XML File</param>
        /// <param name="key">The XML Key of the string needed</param>
        /// <param name="args">Any arguments to fill the strings {0} {n}</param>
        /// <returns></returns>
        public static string GetLocaleString(XDocument file, string key, params object[] args)
        {
            try
            {
                var strings = file.Descendants("string").FirstOrDefault(x => x.Attribute("key")?.Value == key);
                if (strings != null)
                {
                    var values = strings.Descendants("value");
                    return String.Format(values.FirstOrDefault().Value, args).Replace("\\n", Environment.NewLine);
                }
                else
                {
                    throw new Exception($"Error getting string {key} with parameters {(args != null && args.Length > 0 ? args.Aggregate((a, b) => a + "," + b.ToString()) : "none")}");
                }
            }
            catch (Exception e)
            {
                try
                {
                    //try the english string to be sure
                    var strings =
                        Program.LangaugeList.FirstOrDefault(x => x.Name == "English").Doc.Descendants("string").FirstOrDefault(x => x.Attribute("key")?.Value == key);
                    var values = strings?.Descendants("value");
                    if (values != null)
                    {
                        // ReSharper disable once AssignNullToNotNullAttribute
                        return String.Format(values.FirstOrDefault().Value, args).Replace("\\n", Environment.NewLine);
                    }
                    else
                        throw new Exception("Cannot load english string for fallback");
                }
                catch
                {
                    throw new Exception(
                        $"Error getting string {key} with parameters {(args != null && args.Length > 0 ? args.Aggregate((a, b) => a + "," + b.ToString()) : "none")}",
                        e);
                }
            }
        }

        public static string GetNick(Message msg, string[] args, bool sender = false)
        {
            if (sender)
            {
                return $"{msg.From.FirstName} ({msg.From.Id})";
            }
            else if (msg.ReplyToMessage != null)
            {
                return $"{msg.ReplyToMessage.From.FirstName} ({msg.ReplyToMessage.From.Id})";
            }
            else if (args[1] != null)
            {
                return args[1];
            }
            else
            {
                return "";
            }
        }

        public static int GetUserId(Update update, string[] args)
        {
            if (update.Message.ReplyToMessage != null)
            {
                return update.Message.ReplyToMessage.From.Id;
            }
            if (args[1] != null)
            {
                return ResolveIdFromusername(args[1], update.Message.Chat.Id);
            }
            else
            {
                throw new Exception("UnableToResolveId");
            }
        }

        public static int ResolveIdFromusername(string s, long chatId = 0)
        {
            if (chatId != 0)
            {
                var userid = Redis.db.HashGetAsync($"bot:usernames:{chatId}", s).Result;
                var id = 0;
                if (int.TryParse(userid, out id))
                {
                    return id;
                }
                else
                {
                    throw new Exception("UnableToResolveUsername");
                }
            }
            else
            {
                var userid = Redis.db.HashGetAsync($"bot:usernames", s).Result;
                var id = 0;
                if (int.TryParse(userid, out id))
                {
                    return id;
                }
                else
                {
                    throw new Exception("UnableToResolveUsername");
                }
            }
        }

        public static void SaveBan(int userId, string banType)
        {
            Redis.db.HashIncrementAsync($"ban:{userId}", banType);
        }

        public static bool IsGroupAdmin(Update update)
        {
            return IsGroupAdmin(update.Message.From.Id, update.Message.Chat.Id);
        }

        public static bool IsGroupAdmin(int user, long group)
        {
            //fire off admin request
            try
            {
                var admin = Bot.Api.GetChatMemberAsync(group, user).Result;
                return admin.Status == ChatMemberStatus.Administrator || admin.Status == ChatMemberStatus.Creator;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsGlobalAdmin(int id)
        {
            return Constants.GlobalAdmins.Contains(id);
        }

        public static string GetAdminList(Message message, XDocument lang)
        {
            var chatAdmins = Bot.Api.GetChatAdministratorsAsync(message.Chat.Id).Result;
            string creater = "Unknown";
            string adminList = "Unknown";
            foreach (var member in chatAdmins)
            {
                if (member.Status.Equals(ChatMemberStatus.Administrator))
                {
                    adminList = string.Join("\n", member.User.FirstName);
                }
                else if (member.Status.Equals(ChatMemberStatus.Creator))
                {
                    creater = member.User.FirstName;
                }
            }
            return Methods.GetLocaleString(lang, "adminList", creater, adminList);
        }

        public static string GetAbout(long chatId, XDocument lang)
        {
            var about = Redis.db.StringGetAsync($"chat:{chatId}:about").Result;
            if (about.HasValue)
            {
                return GetLocaleString(lang, "about", about);
            }
            else
            {
                return GetLocaleString(lang, "noAbout");
            }
        }

        public static string GetRules(long chatId, XDocument lang)
        {
            var rules = Redis.db.StringGetAsync($"chat:{chatId}:about").Result;
            if (rules.HasValue)
            {
                return GetLocaleString(lang, "rules", rules);
            }
            else
            {
                return GetLocaleString(lang, "noRules");
            }
        }

        public static bool SendInPm(Message updateMessage, string rules)
        {
            var enabled = Redis.db.HashGetAsync($"chat:{updateMessage.Chat.Id}:settings", "Admin_mode");
            return enabled.Equals("yes");
        }

        public static string GetUserInfo(int userid, long? chatId, string chatTitle, XDocument lang)
        {
            var text = GetLocaleString(lang, "userinfoGroup", chatTitle);
            var hash = $"ban:{userid}";
            var banInfo = Redis.db.HashGetAllAsync(hash).Result;
            var completedList = new List<string>();
            completedList.Add(text);
            completedList = banInfo.
                Select(member => GetLocaleString(lang, $"get{member.Name}", member.Value)).ToList();
            if (chatId != null)
            {
                var warns = Redis.db.HashGetAsync($"chat:{chatId}:warns", userid).Result;
                completedList.Add(GetLocaleString(lang, "getwarn", warns));
                warns = Redis.db.HashGetAsync($"chat:{chatId}:mediawarns", userid).Result;
                completedList.Add(GetLocaleString(lang, "getMediaWarn", warns));
            }
            return string.Join("\n", completedList);
        }

        public static bool IsRekt(Update update)
        {
            var isBanned = Redis.db.HashGetAllAsync($"globanBan:{update.Message.From.Id}").Result;
            try
            {
                int banned = int.Parse(isBanned[0].Value);
                var reason = isBanned[1].Value;
                var time = isBanned[2].Value;
                Console.WriteLine($"banned:{banned} reason:{reason}");
                if (banned == 1)
                {
                    var lang = Methods.GetGroupLanguage(update.Message).Doc;
                    try
                    {

                        var temp = BanUser(update.Message.Chat.Id, update.Message.From.Id, lang);
                        SaveBan(update.Message.From.Id, "ban");
                        var temp2 = Bot.Send(GetLocaleString(lang, "globalBan", update.Message.From.FirstName, reason), update);
                    }
                    catch (AggregateException e)
                    {
                        if (e.InnerExceptions[0].Message.Equals("Bad Request: Not enough rights to kick/unban chat member"))
                        {
                            var temp = Bot.Send(GetLocaleString(lang, "botNotAdmin"), update.Message.Chat.Id);
                            return false;
                        }
                        Methods.SendError(e.InnerExceptions[0], update.Message.Chat.Id, lang);
                        return false;
                    }
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                return false;
            }
        }

        public static async Task<bool> BanUser(long chatId, int userId, XDocument doc)
        {
            try
            {
                var res = Bot.Api.KickChatMemberAsync(chatId, userId).Result;
                if (res)
                {
                    await Redis.db.HashIncrementAsync("bot:general", "ban", 1); //Save the number of kicks made by the bot                    
                }
                return res;
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions[0].Message.Equals("Bad Request: Not enough rights to kick/unban chat member"))
                {
                    await Bot.Send(GetLocaleString(doc, "botNotAdmin"), chatId);
                    return false;
                }
                Methods.SendError(e.InnerExceptions[0], chatId, doc);
                return false;
            }
        }

        public static async void AddBanList(long chatId, int userId, string name, string why)
        {
            var hash = $"chat:{chatId}:bannedlist";
            var kvp = new List<KeyValuePair<RedisKey, RedisValue>>();
            kvp.Add(new KeyValuePair<RedisKey, RedisValue>(hash, userId.ToString()));
            await Redis.db.StringSetAsync(kvp.ToArray());
            await Redis.db.HashSetAsync($"{hash}:{userId}", "why", why);
            await Redis.db.HashSetAsync($"{hash}:{userId}", "nick", why);
        }
        public static void CheckTempBans(object state)
        {
            var tempbans = Redis.db.HashGetAllAsync("tempbanned").Result;
            foreach (var mem in tempbans)
            {
                if (System.DateTime.UtcNow.AddHours(2).ToUnixTime() >= int.Parse(mem.Name))
                {
                    var subStrings = mem.Value.ToString().Split(':');
                    var chatId = long.Parse(subStrings[0]);
                    var userId = int.Parse(subStrings[1]);
                    UnbanUser(chatId, userId, GetGroupLanguage(chatId).Doc);
                    Redis.db.HashDeleteAsync("tempbanned", mem.Name);
                    Redis.db.SetRemoveAsync($"chat:{subStrings[0]}:tempbanned", subStrings[1]);
                }

            }
        }

        public static bool UnbanUser(long chatId, int userId, XDocument doc)
        {
            try
            {
                Bot.Api.UnbanChatMemberAsync(chatId, userId);
                Redis.db.SetRemoveAsync($"chat:{chatId}:bannedlist", userId);
                return true;
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions[0].Message.Equals("Bad Request: Not enough rights to kick/unban chat member"))
                {
                    var temp = Bot.Send(GetLocaleString(doc, "botNotAdmin"), chatId);
                    return false;
                }
                Methods.SendError(e.InnerExceptions[0], chatId, doc);
                return false;
            }
        }

        public static string GetMediaId(Message msg)
        {
            if (msg.Photo != null)
            {
                return msg.Photo.Last().FileId;
            }
            if (msg.Document != null)
            {
                return msg.Document.FileId;
            }
            if (msg.Video != null)
            {
                return msg.Video.FileId;
            }
            if (msg.Audio != null)
            {
                return msg.Audio.FileId;
            }
            if (msg.Voice != null)
            {
                return msg.Voice.FileId;
            }
            return msg.Sticker?.FileId;
        }

        public static string GetMediaType(Message msg)
        {
            if (msg.Photo != null)
            {
                return "photo";
            }
            if (msg.Document != null)
            {
                if (msg.Document.MimeType.Equals("video/mp4"))
                {
                    return "gif";
                }
                else
                {
                    return "file";
                }
            }
            if (msg.Video != null)
            {
                return "video";
            }
            if (msg.Audio != null)
            {
                return "audio";
            }
            if (msg.Voice != null)
            {
                return "voice";
            }
            if (msg.Sticker != null)
            {
                return "sticker";
            }
            else
            {
                return null;
            }
        }
    }
}