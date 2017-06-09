using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Newtonsoft.Json;
using StackExchange.Redis;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
#pragma warning disable CS4014
namespace Enforcer5.Helpers
{
    public static class Methods
    {
        public static bool KickUser(long chatId, int userId, XDocument doc)
        {

            try
            {
                var res = Bot.Api.KickChatMemberAsync(chatId, userId, CancellationToken.None).Result;
                if (res)
                {
                     Redis.db.HashIncrementAsync("bot:general", "kick", 1); //Save the number of kicks made by the bot
                    var check =  Bot.Api.GetChatMemberAsync(chatId, userId).Result;
                    var status = check.Status;
                    var count = 0;

                    while (status == ChatMemberStatus.Member && count < 10)
                    {
                        check =  Bot.Api.GetChatMemberAsync(chatId, userId).Result;
                        status = check.Status;
                        count++;
                    }
                    count = 0;
                    while (status != ChatMemberStatus.Left && count < 10)
                    {
                         Bot.Api.UnbanChatMemberAsync(chatId, userId);
                        check =  Bot.Api.GetChatMemberAsync(chatId, userId).Result;
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
                     Bot.Send(GetLocaleString(doc, "botNotAdmin"), chatId);
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
                 Bot.SendReply(GetLocaleString(doc, "Error", exception), updateMessage);
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
            try
            {
                 Bot.Api.SendTextMessageAsync(chatid, GetLocaleString(doc, "Error", arguments));
            }
            catch (ApiRequestException e)
            {
                Console.WriteLine(e);
            }
            catch (AggregateException e)
            {
                Console.WriteLine(e);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
        public static string FormatHTML(this string str)
        {
            return str?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public static Language GetGroupLanguage(User user)
        {
            var language = user.LanguageCode.ToLower();
            var res = Program.LangaugeList.FirstOrDefault(x => x.IEFT == language);
            try
            {
                if (res != null)
                {
                    return res;
                }
                else
                {
                    res = Program.LangaugeList.FirstOrDefault(x => x.IEFT.Contains(language));
                    if (res != null)
                    {
                        return res;
                    }
                    else
                    {
                        return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
                    }
                }
            }
            catch (NullReferenceException e)
            {
                return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
            }
        }

        public static Language GetGroupLanguage(Message uMessage, bool InGroupOnly)
        {
            if (InGroupOnly)
            {
                var lang = Redis.db.StringGetAsync($"chat:{uMessage.Chat.Id}:language").Result;
                if (lang.HasValue)
                {
                    var res = Program.LangaugeList.FirstOrDefault(x => x.Name == lang);
                    try
                    {
                        if (res != null)
                        {
                            return res;
                        }
                        else
                        {
                            return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
                        }
                    }
                    catch (NullReferenceException e)
                    {
                        return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
                    }
                }
                else
                {
                    return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
                }
            }
            else
            {
                var language = uMessage.From.LanguageCode.ToLower();
                var res = Program.LangaugeList.FirstOrDefault(x => x.IEFT == language);
                try
                {
                    if (res != null)
                    {
                        return res;
                    }
                    else
                    {
                        language = language.Substring(0, 2);
                        res = Program.LangaugeList.FirstOrDefault(x => x.IEFT.Contains(language));
                        if (res != null)
                        {
                            return res;
                        }
                        else
                        {
                            return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
                        }
                    }
                }
                catch (NullReferenceException e)
                {
                    return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
                }
            }
            
        
        }

        internal static string GetHelpList(XDocument file)
        {
            var strings = file.Descendants("string").Where(x => x.Attribute("key").Value.Contains("hcommand"));
            var list = new List<string>();
            foreach (var mem in strings)
            {
               list.Add(mem.Attribute("key").Value.Substring("hcommand".Length));
            }
            return string.Join("\n", list);
        }

        public static Language GetGroupLanguage(long chatId)
        {
            var lang = Redis.db.StringGetAsync($"chat:{chatId}:language").Result;
            if (lang.HasValue)
            {
                var res = Program.LangaugeList.FirstOrDefault(x => x.Name == lang);
                try
                {
                    if (res != null)
                    {
                        return res;
                    }
                    else
                    {
                        return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
                    }
                }
                catch (NullReferenceException e)
                {
                    return Program.LangaugeList.FirstOrDefault(x => x.Name == "English");
                }
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
            if (args.Length > 0)
            {
                try
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        args[i] = args[i].ToString().FormatHTML();
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
            try
            {
                var stringsers = file.Descendants("string");
                XElement strings = null;
                foreach (var entry in stringsers)
                {
                    if (entry.Attribute("key").Value.Equals(key))
                    {
                        strings = entry;
                    }
                }
                if (strings != null)
                {
                    var values = strings.Descendants("value");
                    var step1 = String.Format(values.FirstOrDefault().Value, args);
                    step1 = step1.Replace("\\n", Environment.NewLine);
                    return step1;
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
                        var step1 = String.Format(values.FirstOrDefault().Value, args);
                        step1 = step1.Replace("\\n", Environment.NewLine);
                        return step1;
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

        public static string GetLocaleStringNoFormat(XDocument file, string key, params object[] args)
        {
            try
            {
                var strings = file.Descendants("string").FirstOrDefault(x => x.Attribute("key")?.Value == key);
                if (strings != null)
                {
                    var values = strings.Descendants("value");
                    var step1 = String.Format(values.FirstOrDefault().Value, args);
                    step1 = step1.Replace("\\n", Environment.NewLine);
                    return step1;
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
                        var step1 = String.Format(values.FirstOrDefault().Value, args);
                        step1 = step1.Replace("\\n", Environment.NewLine);
                        return step1;
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

        public static string GetNick(Message msg, string[] args, bool sender = false, int userid = 0)
        {
            if (sender)
            {
                return $"{msg.From.FirstName} ({msg.From.Id})";
            }
            if (msg.ReplyToMessage != null)
            {
                return $"{msg.ReplyToMessage.From.FirstName} ({msg.ReplyToMessage.From.Id})";
            }
            if (userid != 0)
            {
                return $"{Redis.db.HashGetAsync($"user:{userid}", "name").Result} ({userid})";
            }
            else
            {
                return "Enforcer";
            }
        }

        public static string GetNick(Message msg, string[] args, int userid = 0, bool sender = false)
        {
            if (sender)
            {
                return $"{msg.From.FirstName} ({msg.From.Id})";
            }
            if (msg.ReplyToMessage != null)
            {
                return $"{msg.ReplyToMessage.From.FirstName} ({msg.ReplyToMessage.From.Id})";
            }
            if (userid != 0)
            {
                return $"{Redis.db.HashGetAsync($"user:{userid}", "name").Result} ({userid})";
            }
            else
            {
                return "Enforcer";
            }
        }

        public static int GetUserId(Update update, string[] args)
        {
            var lang = GetGroupLanguage(update.Message,true).Doc;
            if (update.Message.NewChatMember != null)
            {
                return update.Message.NewChatMember.Id;
            }
            if (update.Message.ReplyToMessage != null)
            {
                return update.Message.ReplyToMessage.From.Id;
            }
            if (args.Length == 2)
            {
                
                if (!string.IsNullOrEmpty(args[1]))
                {
                    int id;
                    if (int.TryParse(args[1], out id))
                    {
                        return id;
                    }
                    if (args[1].Contains(' ') && int.TryParse(args[1].Split(' ')[0], out id))
                    {
                        return id;
                    }
                    var username = update.Message.Entities.Where(x => x.Type == MessageEntityType.TextMention).ToArray();
                    if (username.Length > 0)
                    {
                        return username[0].User.Id;
                    }

                    return ResolveIdFromusername(args[1], update.Message.Chat.Id);
                }
                else
                {
                    return update.Message.From.Id;
                }
            }
            else
            {
                return update.Message.From.Id;
            }
        }

        public static int ResolveIdFromusername(string s, long chatId = 0)
        {
            if (!s.StartsWith("@"))
                throw new Exception("UnableToResolveUsername");
            s = s.ToLower();
            if (s.Contains(' ')) s = s.Split(' ')[0];
            var userid = Redis.db.HashGetAsync($"bot:usernames", s).Result;
            var id = 0;
            if (int.TryParse(userid.ToString(), out id))
            {
                return id;
            }
            else
            {
                throw new Exception("UnableToResolveUsername");
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
            var isAdmin = Redis.db.StringGetAsync($"chat:{group}:adminses:{user}").Result;
            if (isAdmin.Equals("true"))
            {
                return true;
            }
            try
            {
                var admin = Bot.Api.GetChatMemberAsync(group, user).Result;                
                if (admin.Status == ChatMemberStatus.Administrator || admin.Status == ChatMemberStatus.Creator)
                {
                    var set = Redis.db.StringSetAsync($"chat:{group}:adminses:{user}", "true", TimeSpan.FromMinutes(5)).Result;
                    return true;
                }
                else
                {
                    return false;
                }
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
            string adminList = "";
            foreach (var member in chatAdmins)
            {
                if (member.Status.Equals(ChatMemberStatus.Administrator))
                {
                    adminList = $"{adminList}\n{member.User.FirstName}";
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
                return GetLocaleStringNoFormat(lang, "about", about);
            }
            else
            {
                return GetLocaleString(lang, "noAbout");
            }
        }

        public static string  GetRules(long chatId, XDocument lang)
        {
            var rules = Redis.db.StringGetAsync($"chat:{chatId}:rules").Result;
            if (rules.HasValue)
            {
                return GetLocaleStringNoFormat(lang, "rules", rules);
            }
            else
            {
                return GetLocaleString(lang, "noRules");
            }
        }

        public static bool SendInPm(Message updateMessage, string rules)
        {
            var enabled = Redis.db.HashGetAsync($"chat:{updateMessage.Chat.Id}:settings", rules).Result;
            return enabled.Equals("yes");
        }

        public static T[] RemoveAt<T>(T[] source, int index)
        {
            T[] dest = new T[source.Length - 1];
            if (index > 0)
                Array.Copy(source, 0, dest, 0, index);

            if (index < source.Length - 1)
                Array.Copy(source, index + 1, dest, index, source.Length - index - 1);

            return dest;
        }

        public static string GetUserInfo(int userid, long? chatId, string chatTitle, XDocument lang)
        {
            var text = GetLocaleString(lang, "userInfoGlobal");
            var hash = $"ban:{userid}";
            var banInfo = Redis.db.HashGetAllAsync(hash).Result;
            var completedList = new List<string>();
            completedList.Add(text);
            completedList = banInfo.
                Select(member => GetLocaleString(lang, $"get{member.Name.ToString().ToLower()}", member.Value)).ToList();
            if (chatId != null)
            {
                completedList.Add(GetLocaleString(lang, "userinfoGroup", chatTitle));
                int warns = 0;
                if (Redis.db.HashGetAsync($"chat:{chatId}:warns", userid).Result.HasValue)
                {
                    warns = (int) Redis.db.HashGetAsync($"chat:{chatId}:warns", userid).Result;
                }
                completedList.Add(GetLocaleString(lang, "getgroupwarn", warns));
                warns = 0;
                if (Redis.db.HashGetAsync($"chat:{chatId}:mediawarn", userid).Result.HasValue)
                {
                    warns = (int) Redis.db.HashGetAsync($"chat:{chatId}:mediawarn", userid).Result;
                }
                completedList.Add(GetLocaleString(lang, "getMediaWarn", warns));
            }
            
            return string.Join("\n", completedList);
        }

        public static async void IsRekt(Update update)
        {
            if (update.Message.Chat.Type != ChatType.Supergroup)
                return;
            var watch = Redis.db.SetContainsAsync($"chat:{update.Message.Chat.Id}:watch", update.Message.From.Id).Result;
            if (watch) return;
            var isBanned = Redis.db.HashGetAllAsync($"globalBan:{update.Message.From.Id}").Result;
            var name = update.Message.From.FirstName;
            var id = update.Message.From.Id;
            if (update.Message.Type == MessageType.ServiceMessage && update.Message.NewChatMember != null)
            {
                isBanned = Redis.db.HashGetAllAsync($"globalBan:{update.Message.NewChatMember.Id}").Result;
                name = update.Message.NewChatMember.FirstName;
                id = update.Message.NewChatMember.Id;
            }                
            try
            {
                int banned = int.Parse(isBanned[0].Value);                
                if (banned == 1)
                {
                    var reason = isBanned[1].Value;
                    var time = isBanned[2].Value;
                    if (update.Message.Chat.Id == Constants.SupportId)
                    {
                        var notified =  isBanned.Where(e => e.Name.Equals("notified")).FirstOrDefault().Value;
                        if (!notified.HasValue)
                        {
                             Bot.Send($"{name} ({id}) has a global ban record.\nDetails: {reason}", update);
                             Redis.db.HashSetAsync($"globalBan:{id}", "notified", "value");

                        }
                        return;
                    }
                    Console.WriteLine($"Global ban triggered by :{name} reason: {reason}");
                    var lang = Methods.GetGroupLanguage(update.Message,true).Doc;                    
                    
                    try
                    {
                         Bot.Send($"{name} has been banned for {reason} and notified in {update.Message.Chat.Id} {update.Message.Chat.FirstName}", Constants.Devs[0]);
                        var temp = BanUser(update.Message.Chat.Id, update.Message.From.Id, lang);
                        if(temp)
                            SaveBan(update.Message.From.Id, "ban");
                        var temp2 = Bot.Send(GetLocaleString(lang, "globalBan", update.Message.From.FirstName, reason), update);                        
                    }
                    catch (AggregateException e)
                    {
                        if (e.InnerExceptions[0].Message.Equals("Bad Request: Not enough rights to kick/unban chat member"))
                        {
                            var temp = Bot.Send(GetLocaleString(lang, "botNotAdmin"), update.Message.Chat.Id);
                            return;
                        }
                        Methods.SendError(e.InnerExceptions[0], update.Message.Chat.Id, lang);
                        return;
                    }
                    return;
                }
                else
                {
                    return;
                }
            }
            catch (Exception e)
            {
                return;
            }
        }

        public static bool BanUser(long chatId, int userId, XDocument doc)
        {
            try
            {
                var res = Bot.Api.KickChatMemberAsync(chatId, userId).Result;
                if (res)
                {
                     Redis.db.HashIncrementAsync("bot:general", "ban", 1); //Save the number of kicks made by the bot                    
                }
                return res;
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions[0].Message.Equals("Bad Request: Not enough rights to kick/unban chat member"))
                {
                     Bot.Send(GetLocaleString(doc, "botNotAdmin"), chatId);
                    return false;
                }
                if (e.InnerExceptions[0].Message.Contains("USER_ADMIN_INVALID"))
                {
                    Bot.Send(GetLocaleString(doc, "cannotbanadmin"), chatId);
                    return false;
                }
                Methods.SendError(e.InnerExceptions[0], chatId, doc);
                return false;
            }
        }

        public static void AddBanList(long chatId, int userId, string name, string why)
        {
            var hash = $"chat:{chatId}:bannedlist";
            var kvp = new List<KeyValuePair<RedisKey, RedisValue>>();
            kvp.Add(new KeyValuePair<RedisKey, RedisValue>(hash, userId.ToString()));
             Redis.db.StringSetAsync(kvp.ToArray());
             Redis.db.HashSetAsync($"{hash}:{userId}", "why", why);
             Redis.db.HashSetAsync($"{hash}:{userId}", "nick", why);
        }
        internal static void CheckTempBans(object obj)
        {
            try
            {
#if normal
                var tempbans = Redis.db.HashGetAllAsync("tempbanned").Result;
#endif
#if premium
                var tempbans = Redis.db.HashGetAllAsync("tempbannedPremium").Result;
#endif
                foreach (var mem in tempbans)
                {
                    try
                    {
                        if (System.DateTime.UtcNow.AddHours(2).ToUnixTime() >= long.Parse(mem.Name))
                        {
                            var subStrings = mem.Value.ToString().Split(':');
                            var chatId = long.Parse(subStrings[0]);
                            var userId = int.Parse(subStrings[1]);
                            UnbanUser(chatId, userId, GetGroupLanguage(chatId).Doc);
#if normal
                            Redis.db.HashDeleteAsync("tempbanned", mem.Name);
                            Redis.db.SetRemoveAsync($"chat:{subStrings[0]}:tempbanned", subStrings[1]);
#endif
#if premium
                        Redis.db.HashDeleteAsync("tempbannedPremium", mem.Name);
                        Redis.db.SetRemoveAsync($"chat:{subStrings[0]}:tempbannedPremium", subStrings[1]);
#endif
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        Bot.Send($"{e.Message}", Constants.Devs[0]);
                    }

                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Bot.Send($"{e.Message}", Constants.Devs[0]);
            }
        }

        public static bool UnbanUser(long chatId, int userId, XDocument doc)
        {
            try
            {
                var res = Bot.Api.UnbanChatMemberAsync(chatId, userId);
                while (!res.Result)
                {
                    res = Bot.Api.UnbanChatMemberAsync(chatId, userId);
                    
                }
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
            if (msg.ReplyToMessage != null)
            {
                if (msg.ReplyToMessage.Photo != null)
                {
                    return msg.ReplyToMessage.Photo.Last().FileId;
                }
                if (msg.ReplyToMessage.Document != null)
                {
                    return msg.ReplyToMessage.Document.FileId;
                }

                if (msg.ReplyToMessage.Voice != null)
                {
                    return msg.ReplyToMessage.Voice.FileId;
                }
                if (msg.ReplyToMessage.Sticker != null)
                {
                    return msg.ReplyToMessage.Sticker?.FileId;
                }
                if (msg.ReplyToMessage.Video != null)
                {
                    return msg.ReplyToMessage.Video.FileId;
                }
                if (msg.ReplyToMessage.Audio != null)
                {
                    return msg.ReplyToMessage.Audio.FileId;
                }
                if (msg.ReplyToMessage.Text != null)
                {
                    return msg.ReplyToMessage.MessageId.ToString();
                }
                if (msg.ReplyToMessage.VideoNote != null)
                {
                    return msg.ReplyToMessage.VideoNote.FileId;
                }
                else
                {
                    return "unknown";
                }

            }
            else
            {
                if (msg.Photo != null)
                {
                    return msg.Photo.Last().FileId;
                }
                if (msg.Document != null)
                {
                    return msg.Document.FileId;
                }

                if (msg.Voice != null)
                {
                    return msg.Voice.FileId;
                }
                if (msg.Sticker != null)
                {
                    return msg.Sticker?.FileId;
                }
                if (msg.Video != null)
                {
                    return msg.Video.FileId;
                }
                if (msg.Audio != null)
                {
                    return msg.Audio.FileId;
                }
                if (msg.Text != null)
                {
                    return msg.MessageId.ToString();
                }
                else
                {
                    return "unknown";
                }

            }
        }

        public static string RLEDecode(string input)
        {
            var runLengthEncodedString = new StringBuilder();
            var baseString = input;

            var radix = 0;

            for (var i = 0; i < baseString.Length; i++)
            {
                if (char.IsNumber(baseString[i]))
                {
                    radix++;
                }
                else
                {
                    if (radix > 0)
                    {
                        var valueRepeat = Convert.ToInt32(baseString.Substring(i - radix, radix));

                        for (var j = 0; j < valueRepeat; j++)
                        {
                            runLengthEncodedString.Append(baseString[i]);
                        }

                        radix = 0;
                    }
                    else if (radix == 0)
                    {
                        runLengthEncodedString.Append(baseString[i]);
                    }
                }
            }

            if (!HasChar(runLengthEncodedString))
            {
                throw new Exception("\r\nCan't to decode! Input string has the wrong syntax. There isn't any char (e.g. 'a'->'z') in your input string, there was/were only number(s).\r\n");
            }

            return runLengthEncodedString.ToString();
        }

        private static bool HasChar(StringBuilder input)
        {
            for (var i = 0; i < input.Length; i++)
            {
                if (char.IsLetter(input[i]))
                {
                    return true;
                }
            }

            return false;
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
            if (msg.Voice != null)
            {
                return "voice";
            }
            if (msg.VideoNote != null)
            {
                return "videoNote";
            }
            if (msg.Sticker != null)
            {
                return "sticker";
            }
            if (msg.Video != null)
            {
                return "video";
            }
            if (msg.Audio != null)
            {
                return "audio";
            }
            if (msg.Text != null)
            {
                return "text";
            }
            else
            {
                return "unknown";
            }
        }

        public static string GetContentType(Message msg)
        {
            if (msg.Type == null) return "unknown";
            switch (msg.Type)
            {
                case MessageType.TextMessage:
                       var text = msg.Text;
                    var link = msg.Entities.Where(x => x.Type == MessageEntityType.Url).ToArray();
                    if (link.Length > 0)
                        return "link";
                    link = msg.Entities.Where(x => x.Type == MessageEntityType.TextLink).ToArray();
                    if (link.Length > 0)
                        return "link";
                    return "text";
                    break;
                case MessageType.PhotoMessage:
                    return "image";
                    break;
                case MessageType.DocumentMessage:
                    if (msg.Document.MimeType == null) return "unknown";
                    if (msg.Document.MimeType.Equals("video/mp4"))
                    {
                        return "gif";
                    }
                    else
                    {
                        return "file";
                    }
                    break;
                case MessageType.VoiceMessage:
                    return "voice";
                    break;
                case MessageType.StickerMessage:
                    return "sticker";
                    break;
                case MessageType.VideoMessage:
                    return "video";
                    break;
                case MessageType.AudioMessage:
                    return "audio";
                    break;
                case MessageType.ContactMessage:
                    return "contact";
                    break;                
                default:
                    return "unknown";
                    break;
            }
        }

        public static bool IsBanned(long chatId, int userId)
        {
            var hash = $"chat:{chatId}:banned";
            var res = Redis.db.SetContainsAsync(hash, userId).Result;
            return res;
        }

        internal static Language SelectLanguage(string command, string[] args, ref InlineKeyboardMarkup menu, bool addAllbutton = true)
        {
            var langs = Program.LangaugeList;
            var isBase = args[4] == "base";
            if (isBase)
            {
                var variants = langs.Where(x => x.Base == args[2]).ToList();
                if (variants.Count() > 1)
                {
                    var buttons = new List<InlineKeyboardButton>();
                    buttons.AddRange(variants.Select(x => new InlineKeyboardButton(x.Base, $"{command}:{args[1]}:{x.Base}:{x.Base}:v")));
                    if (addAllbutton)
                        buttons.Insert(0, new InlineKeyboardButton("All", $"{command}:{args[1]}:{args[2]}:All:v"));

                    var twoMenu = new List<InlineKeyboardButton[]>();
                    for (var i = 0; i < buttons.Count; i++)
                    {
                        if (buttons.Count - 1 == i)
                        {
                            twoMenu.Add(new[] { buttons[i] });
                        }
                        else
                            twoMenu.Add(new[] { buttons[i], buttons[i + 1] });
                        i++;
                    }
                    menu = new InlineKeyboardMarkup(twoMenu.ToArray());

                    return null;
                }
                else
                    return variants.First();
            }
            else
            {
                return langs.First(x => x.Base == args[2]);
            }
        }

        public static bool IsLangAdmin(int id)
        {
            var res = Redis.db.SetContainsAsync($"langAdmins", id).Result;
            return res;
        }

        public static bool IsGroupAdmin(CallbackQuery update)
        {
            return IsGroupAdmin(update.Message.From.Id, update.Message.Chat.Id);
        }

        public static void SetGroupLang(string newLang, long chatId)
        {
            Redis.db.StringSetAsync($"chat:{chatId}:language", newLang);
        }

        internal static void Restart(object obj)
        {
            while (true)
            {
                try
                {
                    var runningTime = DateTime.UtcNow - Bot.StartTime;
                    if (runningTime >= TimeSpan.FromMinutes(45))
                    {
                        Bot.Api.SendTextMessageAsync(Constants.Devs[0], "Schedualed Restart");
                        Environment.Exit(0);
                    }                                        
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                Thread.Sleep(TimeSpan.FromMinutes(10));
            }
        }

        public static int GetGroupTempbanTime(long chatId)
        {
            int res = 1440;
            var time = Redis.db.HashGetAsync($"chat:{chatId}:otherSettings", "tempbanTime").Result;
            if (int.TryParse(time.ToString(), out res))
            {
                return res;
            }
            return res;
        }

        public static object GetUsername(int id)
        {
            var username = Redis.db.HashGetAsync($"user:{id}", "username").Result;
            if (username.HasValue)
                return username.ToString();
            return "No username found";
        }
    }
}
