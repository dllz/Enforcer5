using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
#pragma warning disable CS4014
#pragma warning disable CS0168
namespace Enforcer5
{
    public static class OnMessage
    {
        public static void AntiFlood(Update update)
        {
            try
            {
                var time = (DateTime.UtcNow - update.Message.Date);
                if (time.TotalSeconds > 5) return;
                var chatId = update.Message.Chat.Id;
                var flood = Redis.db.HashGetAsync($"chat:{chatId}:settings", "Flood").Result;
                if (flood.Equals("yes")) return;
                var watch = Redis.db.SetContainsAsync($"chat:{chatId}:watch", update.Message.From.Id).Result;
                if (watch) return;
                new Task(() => { AntiLength(update); }).Start();
                var msgType = Methods.GetContentType(update.Message);
                XDocument lang;
                try
                {
                    lang = Methods.GetGroupLanguage(update.Message).Doc;
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

                    var msgs = Redis.db.StringGetAsync($"spam:{chatId}:{update.Message.From.Id}").Result;
                    int num = msgs.HasValue ? int.Parse(msgs.ToString()) : 0;   
                    if (num == 0) num = 1;
                    var maxSpam = 8;
                    if (update.Message.Chat.Type == ChatType.Private) maxSpam = 12;
                    var floodSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:flood").Result;
                    var maxMsgs = floodSettings.Where(e => e.Name.Equals("MaxFlood")).FirstOrDefault();
                    var maxTime = TimeSpan.FromSeconds(5);
                    int maxmsgs;
                    Redis.db.StringSetAsync($"spam:{chatId}:{update.Message.From.Id}", num + 1, maxTime);
                    if (int.TryParse(maxMsgs.Value, out maxmsgs))
                    {
                       // Bot.Send($"{num} of {maxmsgs}", update);
                        if (num == (int.Parse(maxMsgs.Value) + 1))
                        {
                            var action = floodSettings.Where(e => e.Name.Equals("ActionFlood")).FirstOrDefault();
                            var name = update.Message.From.FirstName;
                            if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
                            try
                            {
                                var userid = update.Message.From.Id;
                                var groupId = update.Message.Chat.Id;
                                switch (action.Value.ToString())
                                {
                                    case "kick":
                                        Methods.KickUser(chatId, update.Message.From.Id, lang);
                                       
                                        Bot.Send(
                                            Methods.GetLocaleString(lang, "kickedForFlood", $"{name}, {update.Message.From.Id}"),
                                            update);
                                    break;
                                    case "ban":
                                    Methods.BanUser(chatId, update.Message.From.Id, lang);
                                        Methods.SaveBan(update.Message.From.Id, "flood");
                                        Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                            Methods.GetLocaleString(lang, "bannedForFlood", ".."));
                                        Bot.Send(Methods.GetLocaleString(lang, "bannedForFlood", name), update);
                                    break;
                                    case "warn":
                                        Commands.Warn(userid, groupId, update, targetnick:userid.ToString());
                                    break;
                                case "tempban":

                                    var time2= Methods.GetGroupTempbanTime(groupId);
                                    var timeBanned = TimeSpan.FromMinutes(time2);
                                    string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                                    var message = Methods.GetLocaleString(lang, "tempbannedForFlood",
                                        $"{update.Message.From.Id}", timeText);
                                    Commands.Tempban(userid, groupId, time2, userid.ToString(), message: message);
                                    break;
                             }

                            }
                            catch (Exception e)
                            {
                                
                            }
                        }
                    }
                
            }
            catch (Exception e)
            {
                Bot.Send($"{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }
        }

        private static void AntiTextLenght(Update update)
        {
            var groupId = update.Message.Chat.Id;
            var settings = Redis.db.HashGetAllAsync($"chat:{groupId}:antitextlengthsettings").Result;
            var enabled = settings.Where(e => e.Name.Equals("enabled")).FirstOrDefault();
            if (enabled.Value.Equals("yes"))
            {                
                var text = update.Message.Text;
                var chartext = text.ToCharArray();
                int intml;
                int intmline;
                settings.Where(e => e.Name.Equals("maxlength")).FirstOrDefault().Value.TryParse(out intml);
                settings.Where(e => e.Name.Equals("maxlines")).FirstOrDefault().Value.TryParse(out intmline);
                var lines = Regex.Split(text, "(.+?)(?:\r\n|\n)");
                if (text.Length >= intml || lines.Length >= intmline)
                {
                    var action = settings.Where(e => e.Name.Equals("action")).FirstOrDefault();
                    XDocument lang;
                    try
                    {
                        lang = Methods.GetGroupLanguage(update.Message).Doc;
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
                    var userid = update.Message.From.Id;
                    string reply;
                    switch (action.Value)
                    {
                        case "kick":
                            Methods.KickUser(groupId, userid, lang);
                            reply = Methods.GetLocaleString(lang, "kickformesslength", userid);
                            Service.LogBotAction(groupId, reply);
                            Bot.SendReply(reply, update);
                            return;
                            break;
                        case "ban":
                            Methods.BanUser(groupId, userid, lang);
                            Methods.SaveBan(userid, "longmessages");
                            reply = Methods.GetLocaleString(lang, "banformesslength", userid);
                            Service.LogBotAction(groupId, reply);
                            Bot.SendReply(Methods.GetLocaleString(lang, "banformesslength", userid), update);
                            return;
                            break;
                        case "Warn":
                            Commands.Warn(userid, groupId, update, targetnick:userid.ToString());
                            return;
                            break;
                        case "tempban":

                            var time = Methods.GetGroupTempbanTime(groupId);
                            var timeBanned = TimeSpan.FromMinutes(time);
                            string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                            var message = Methods.GetLocaleString(lang, "tempbanformesslength",
                                $"{update.Message.From.Id}", timeText);
                            Service.LogBotAction(groupId, message);
                            Commands.Tempban(userid, groupId, time, userid.ToString(), message: message);
                            break;
                        case "default":
                            Bot.SendReply(Methods.GetLocaleString(lang, "actionNotSettext"), update);
                            break;
                    }
                }
            }
        }

        private static void AntiNameLength(Update update)
        {
            var groupId = update.Message.Chat.Id;
            var settings = Redis.db.HashGetAllAsync($"chat:{groupId}:antinamelengthsettings").Result;
            var enabled = settings.Where(e => e.Name.Equals("enabled")).FirstOrDefault();
            if (enabled.Value.Equals("yes"))
            {
                var text = update.Message.From.FirstName;
                text = $"{text}{update.Message.From.LastName}";
                int intml;
                settings.Where(e => e.Name.Equals("maxlength")).FirstOrDefault().Value.TryParse(out intml);
                if (text.Length >= intml)
                {
                    var action = settings.Where(e => e.Name.Equals("action")).FirstOrDefault();
                    XDocument lang;
                    try
                    {
                        lang = Methods.GetGroupLanguage(update.Message).Doc;
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
                    var userid = update.Message.From.Id;
                    string reply;
                    switch (action.Value)
                    {
                        case "kick":
                            Methods.KickUser(groupId, userid, lang);
                            reply = Methods.GetLocaleString(lang, "kickfornamelength", userid);
                            Service.LogBotAction(groupId, reply);
                            Bot.SendReply(reply, update);
                            break;
                        case "ban":
                            Methods.BanUser(groupId, userid, lang);
                            Methods.SaveBan(userid, "namelength");
                            reply = Methods.GetLocaleString(lang, "banfornamelength", userid);
                            Service.LogBotAction(groupId, reply);
                            Bot.SendReply(reply, update);
                            break;
                        case "Warn":
                            Commands.Warn(userid, groupId, update, targetnick: userid.ToString());
                            break;
                        case "tempban":
                            var time = Methods.GetGroupTempbanTime(groupId);
                            var timeBanned = TimeSpan.FromMinutes(time);
                            string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                            var message = Methods.GetLocaleString(lang, "tempbanfornamelength",
                                $"{update.Message.From.Id}", timeText);
                            Service.LogBotAction(groupId, message);
                            Commands.Tempban(userid, groupId, time, userid.ToString(), message:message);
                            break;
                        case "default":
                            Bot.SendReply(Methods.GetLocaleString(lang, "actionNotSetname"), update);
                            break;
                    }
                }

            }
        }

        public static void AntiLength(Update update)
        {
            try
            {
                AntiNameLength(update);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Bot.Send($"@falconza shit happened\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }
            try
            {
                if(update.Message.Type == MessageType.TextMessage)
                    AntiTextLenght(update);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Bot.Send($"@falconza shit happened\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }
        }

        public static void CheckMedia(Update update)
        {
            try
            {                
                var chatId = update.Message.Chat.Id;
                var watch = Redis.db.SetContainsAsync($"chat:{chatId}:watch", update.Message.From.Id).Result;
                if (watch) return;
                var media = Methods.GetContentType(update.Message);
                var status = Redis.db.HashGetAsync($"chat:{chatId}:media", media).Result;
                XDocument lang;
                try
                {
                    lang = Methods.GetGroupLanguage(update.Message).Doc;
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
                var allowed = status.Equals("allowed");
                if (allowed == false && status.HasValue)
                {
                    var name = $"{update.Message.From.FirstName} [{update.Message.From.Id}]";
                    if (update.Message.From.Username != null)
                        name = $"{name} (@{update.Message.From.Username})";
                    var max = Redis.db.HashGetAsync($"chat:{chatId}:Warnsettings", "mediamax").Result.HasValue
                        ? Redis.db.HashGetAsync($"chat:{chatId}:Warnsettings", "mediamax").Result
                        : 2;
                    var current = Redis.db.HashIncrementAsync($"chat:{chatId}:mediawarn", update.Message.From.Id, 1).Result;
                    if (current >= int.Parse(max))
                    {
                        string reply;
                        switch (status)
                        {
                            case "kick":

                                Methods.KickUser(chatId, update.Message.From.Id, lang);
                                reply = Methods.GetLocaleString(lang, "kickedformedia", $"{name}");
                                Service.LogBotAction(chatId, reply);
                                Bot.SendReply(
                                    reply,
                                    update);
                                break;
                            case "ban":
                                Methods.BanUser(chatId, update.Message.From.Id, lang);
                                Methods.SaveBan(update.Message.From.Id, "media");
                                reply = Methods.GetLocaleString(lang, "bannedformedia", name);
                                Service.LogBotAction(chatId, reply);
                                Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                    Methods.GetLocaleString(lang, "bannedformedia", ""));
                                Bot.SendReply(reply, update);
                                break;

                            case "tempban":
                                var time = Methods.GetGroupTempbanTime(chatId);
                                var timeBanned = TimeSpan.FromMinutes(time);
                                string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                                var message = Methods.GetLocaleString(lang, "tempbannedformedia",
                                    $"{name}, {update.Message.From.Id}", timeText);
                                Service.LogBotAction(chatId, message);
                                Commands.Tempban(update.Message.From.Id, chatId, time, update.Message.From.Id.ToString(), message:message);
                                break;
                        }
                    }
                    else
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, "mediaNotAllowed", current, max),
                            update);
                    }

                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Bot.Send($"{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }
        }

        public static void RightToLeft(Update update)
        {
            try
            {
                var msgType = Methods.GetMediaType(update.Message);
                var chatId = update.Message.Chat.Id;
                var watch = Redis.db.SetContainsAsync($"chat:{chatId}:watch", update.Message.From.Id).Result;
                if (watch) return;
                XDocument lang;
                try
                {
                    lang = Methods.GetGroupLanguage(update.Message).Doc;
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
                var rtlStatus = Redis.db.HashGetAsync($"chat:{chatId}:char", "Rtl").Result;
                var status = rtlStatus.HasValue ? rtlStatus.ToString() : "allowed";
                if (status.Equals("ban") || status.Equals("kick"))
                {
                    var name = update.Message.From.FirstName;
                    var rtl = "‮";
                    var lastName = "x";
                    if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
                    if (update.Message.From.LastName != null) lastName = update.Message.From.LastName;
                    var text = update.Message.Text;
                    bool check = text.Contains(rtl) || name.Contains(rtl) || lastName.Contains(rtl);
                    try
                    {
                        if (check)
                        {
                            string reply;
                            switch (status)
                            {
                                case "kick":

                                    Methods.KickUser(chatId, update.Message.From.Id, lang);
                                    reply = Methods.GetLocaleString(lang, "kickedForRtl",
                                        $"{name}, {update.Message.From.Id}");
                                    Service.LogBotAction(chatId, reply);
                                    Bot.Send(
                                        reply,
                                        update);
                                    break;
                                case "ban":
                                    Methods.BanUser(chatId, update.Message.From.Id, lang);
                                    Methods.SaveBan(update.Message.From.Id, "rtl");
                                    Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                        Methods.GetLocaleString(lang, "bannedForRtl", ""));
                                    reply = Methods.GetLocaleString(lang, "bannedForRtl", $"{name}, {update.Message.From.Id}")''
                                    Service.LogBotAction(chatId, reply);
                                    Bot.Send(
                                        reply,
                                        update);
                                    break;

                                case "tempban":
                                    var time = Methods.GetGroupTempbanTime(chatId);
                                    var timeBanned = TimeSpan.FromMinutes(time);
                                    string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                                    var message = Methods.GetLocaleString(lang, "tempbannedForRtl",
                                        $"{name}, {update.Message.From.Id}", timeText);
                                    Service.LogBotAction(chatId, message);
                                    Commands.Tempban(update.Message.From.Id, chatId,time, update.Message.From.Id.ToString(), message:message);
                                    break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Bot.Send($"{e.Message}\n\n{e.StackTrace}", -1001076212715);
                    }

                }               
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Bot.Send($"{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }
        }

        public static void ArabDetection (Update update)
        {
            var chatId = update.Message.Chat.Id;
            var watch = Redis.db.SetContainsAsync($"chat:{chatId}:watch", update.Message.From.Id).Result;
            if (watch) return;
            var arabStatus = Redis.db.HashGetAsync($"chat:{chatId}:char", "Arab").Result.ToString();
            if (string.IsNullOrEmpty(arabStatus)) arabStatus = "allowed";
            if (!arabStatus.Equals("allowed"))
            {
                var arabicChars = "[ساینبتسیکبدثصکبثحصخبدوزطئظضچج]";
                var text = $"{update.Message.Text} {update.Message.From.FirstName} {update.Message.From.LastName} {update.Message.ForwardFrom?.FirstName} {update.Message.ForwardFrom?.LastName} {update.Message.From.Username} {update.Message.ForwardFrom?.Username}";
                var found = false;
                for (int i = 0; i < text.Length; i++)
                {
                   
                        //var letter = char.ConvertToUtf32(text[i], text[i + 1]);
                        found = Regex.IsMatch(text[i].ToString(), arabicChars);
                        if (found)
                        {
                            break;
                        }
                                        
                }

                if (found)
                {                   
                    var lang = Methods.GetGroupLanguage(update.Message).Doc;
                    var name = update.Message.From.FirstName;
                    var lastName = "x";
                    if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
                    if (update.Message.From.LastName != null) lastName = update.Message.From.LastName;
                    try
                    {
                        string reply;
                        switch (arabStatus)
                        {
                            case "kick":
                                Methods.KickUser(chatId, update.Message.From.Id, lang);
                                reply = Methods.GetLocaleString(lang, "kickedForNoEnglishScript",
                                    $"{name}, {update.Message.From.Id}");
                                Service.LogBotAction(chatId, reply);
                                Bot.Send(
                                    reply,
                                    update);
                                break;
                            case "ban":
                                Methods.BanUser(chatId, update.Message.From.Id, lang);
                                Methods.SaveBan(update.Message.From.Id, "arab");
                                Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                    Methods.GetLocaleString(lang, "bannedForNoEnglishScript", "."));

                                reply = Methods.GetLocaleString(lang, "bannedForNoEnglishScript",
                                    $"{name}, {update.Message.From.Id}");
                                Service.LogBotAction(chatId, reply);
                                Bot.Send(
                                    reply,
                                    update);
                                break;

                            case "tempban":
                                var time = Methods.GetGroupTempbanTime(chatId);
                                var timeBanned = TimeSpan.FromMinutes(time);
                                string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                                var message = Methods.GetLocaleString(lang, "tempbanForNoEnglishScript",
                                    $"{name}, {update.Message.From.Id}", timeText);
                                Service.LogBotAction(chatId, message);
                                Commands.Tempban(update.Message.From.Id, chatId,time, update.Message.From.Id.ToString(), message:message);
                                break;
                        }
                    }
                    catch (Exception e)
                    {

                    }                    

                }
            }
            RightToLeft(update);
        }

        public static bool isIgnored(long chatId, string msgType)
        {
            var status = Redis.db.HashGetAsync($"chat:{chatId}:floodexceptions", msgType).Result;
            if (status.HasValue)
            {
                return status.Equals("no");
            }
            else
            {
                return true;
            }
        }
    }
}
