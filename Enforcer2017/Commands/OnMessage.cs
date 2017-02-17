using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    public static class OnMessage
    {
        public static async Task AntiFlood(Update update)
        {
            try
            {
                var time = (DateTime.UtcNow - update.Message.Date);
                if (time.TotalSeconds > 5) return;
                var msgType = Methods.GetMediaType(update.Message);
                var chatId = update.Message.Chat.Id;
                var lang = Methods.GetGroupLanguage(update.Message).Doc;
                if (isIgnored(chatId, msgType))
                {
                    var msgs = Redis.db.StringGetAsync($"spam:{chatId}:{update.Message.From.Id}").Result;
                    int num = msgs.IsInteger ? int.Parse(msgs) : 0;
                    if (num == 0) num = 1;
                    var maxSpam = 8;
                    if (update.Message.Chat.Type == ChatType.Private) maxSpam = 12;
                    var floodSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:flood").Result;
                    var maxMsgs = floodSettings.Where(e => e.Name.Equals("MaxFlood")).FirstOrDefault();
                    var maxTime = TimeSpan.FromSeconds(5);
                    int maxmsgs;
                    if ((DateTime.Now.ToUnixTime() - update.Message.Date.ToUnixTime()) < 30)
                    {
                        await Redis.db.StringSetAsync($"spam:{chatId}:{update.Message.From.Id}", num + 1, maxTime);
                    }
                    if (int.TryParse(maxMsgs.Value, out maxmsgs))   
                    {
                        if (num == (int.Parse(maxMsgs.Value) + 1))
                        {
                            var action = floodSettings.Where(e => e.Name.Equals("ActionFlood")).FirstOrDefault();
                            var name = update.Message.From.FirstName;
                            if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
                            try
                            {
                                if (action.Value.Equals("ban"))
                                {
                                    await Methods.BanUser(chatId, update.Message.From.Id, lang);
                                    Methods.SaveBan(update.Message.From.Id, "flood");
                                    Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                        Methods.GetLocaleString(lang, "bannedForFlood", ".."));
                                    await Bot.Send(Methods.GetLocaleString(lang, "bannedForFlood", name), update);
                                }
                                else
                                {
                                    await Methods.KickUser(chatId, update.Message.From.Id, lang);
                                    Methods.SaveBan(update.Message.From.Id, "flood");
                                    await Bot.Send(
                                        Methods.GetLocaleString(lang, "kickedForFlood", $"{name}, {update.Message.From.Id}"),
                                        update);
                                }
                            }
                            catch (Exception e)
                            {
                                throw;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                await Bot.Send($"{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }
        }

        public static async Task CheckMedia(Update update)
        {
            try
            {
                var name = $"{update.Message.From.FirstName} [{update.Message.From.Id}]";
                if (update.Message.From.Username != null)
                    name = $"{name} (@{update.Message.From.Username})";
                var chatId = update.Message.Chat.Id;
                var media = Methods.GetMediaType(update.Message);
                var status = Redis.db.HashGetAsync($"chat:{chatId}:media", media).Result;
                var lang = Methods.GetGroupLanguage(update.Message).Doc;
                if (!status.Equals("allowed"))
                {
                    var max = Redis.db.HashGetAsync($"chat:{chatId}:Warnsettings", "mediamax").Result.HasValue
                        ? Redis.db.HashGetAsync($"chat:{chatId}:Warnsettings", "mediamax").Result
                        : 2;
                    var current = Redis.db.HashIncrementAsync($"chat:{chatId}:mediawarn", update.Message.From.Id, 1).Result;
                    if (current >= int.Parse(max))
                    {
                        if (status.Equals("ban"))
                        {
                            await Methods.BanUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "media");
                            Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                Methods.GetLocaleString(lang, "bannedformedia", ""));
                            await Bot.SendReply(Methods.GetLocaleString(lang, "bannedformedia", name), update);
                        }
                        else
                        {
                            await Methods.KickUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "media");
                            await Bot.SendReply(
                                Methods.GetLocaleString(lang, "kickedformedia", $"{name}"),
                                update);
                        }
                    }
                    else
                    {
                        await Bot.SendReply(Methods.GetLocaleString(lang, "mediaNotAllowed", current, max),
                            update);
                    }

                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                await Bot.Send($"{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }
        }
        public static async Task RightToLeft(Update update)
        {
            try
            {
                var msgType = Methods.GetMediaType(update.Message);
                var chatId = update.Message.Chat.Id;
                var lang = Methods.GetGroupLanguage(update.Message).Doc;
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
                            if (status.Equals("kick"))
                            {
                                await Methods.KickUser(chatId, update.Message.From.Id, lang);
                                Methods.SaveBan(update.Message.From.Id, "rtl");
                                await Bot.Send(
                                    Methods.GetLocaleString(lang, "kickedForRtl", $"{name}, {update.Message.From.Id}"),
                                    update);
                            }
                            else
                            {
                                await Methods.BanUser(chatId, update.Message.From.Id, lang);
                                Methods.SaveBan(update.Message.From.Id, "rtl");
                                Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                    Methods.GetLocaleString(lang, "bannedForRtl", "."));
                                await Bot.Send(
                                        Methods.GetLocaleString(lang, "bannedForRtl", $"{name}, {update.Message.From.Id}"),
                                        update);
                            }                            
                        }
                    }
                    catch (Exception e)
                    {
                        await Bot.Send($"{e.Message}\n\n{e.StackTrace}", -1001076212715);
                    }

                }               
                //var banDetails = Redis.db.HashGetAllAsync($"globanBan:{update.Message.From.Id}").Result;
                //var isBanned = banDetails.Where(e => e.Name.Equals("banned")).FirstOrDefault();
                //var seenSupport = banDetails.Where(e => e.Name.Equals("seen")).FirstOrDefault();
                //if (isBanned.Value.Equals("1"))
                //{
                //    if (update.Message.Chat.Id != Constants.SupportId)
                //    {
                //        try
                //        {
                //            await Methods.BanUser(chatId, update.Message.From.Id, lang);
                //            var motivation = banDetails.Where(e => e.Name.Equals("motivation")).FirstOrDefault();
                //            Methods.AddBanList(chatId, update.Message.From.Id, $"{update.Message.From.FirstName} ({update.Message.From.Id})", $"Global banned for: {motivation}");
                //            await Bot.Send(
                //                Methods.GetLocaleString(lang, "globalBanNotif",
                //                    $"{update.Message.From.FirstName}, {update.Message.From.Id}", motivation), update);
                //        }
                //        catch (Exception e)
                //        {

                //        }
                //        await Bot.Send(
                //                $"{update.Message.From.FirstName}, {update.Message.From.Id} has been notified of ban in {chatId} {update.Message.Chat.Title}",
                //                Constants.Devs[0]);
                //    }
                //}
                //else if (seenSupport.Name.Equals("1"))
                //{
                //    var motivation = banDetails.Where(e => e.Name.Equals("motivation")).FirstOrDefault();
                //    await Redis.db.HashSetAsync($"globalBan:{update.Message.From.Id}", "seen", 1);
                //    await Bot.Send(
                //        $"{update.Message.From.FirstName} has a history of {motivation} and has joined @werewolfsupport to appeal there global ban", update);
                //}
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                await Bot.Send($"{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }
        }

        public static async Task ArabDetection (Update update)
        {
            var chatId = update.Message.Chat.Id;
            var arabStatus = Redis.db.HashGetAsync($"chat:{chatId}:char", "Arab").Result.ToString();
            if (string.IsNullOrEmpty(arabStatus)) arabStatus = "allowed";
            if (!arabStatus.Equals("allowed"))
            {
                var arabicChars = "ساینبتسیکبدثصکبثحصخبدوزطئظضچج";
                var text = $"{update.Message.Text} {update.Message.From.FirstName} {update.Message.From.LastName} {update.Message.ForwardFrom?.FirstName} {update.Message.ForwardFrom?.LastName} {update.Message.From.Username} {update.Message.ForwardFrom?.Username}";
                var found = false;
                for (int i = 0; i < text.Length; i++)
                {
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
                        if (arabStatus.Equals("kick"))
                        {
                            await Methods.KickUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "arab");
                        }
                        else
                        {
                            await Methods.BanUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "rtl");
                            Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                Methods.GetLocaleString(lang, "bannedForNoEnglishScript", "."));
                        }
                        await Bot.Send(
                                Methods.GetLocaleString(lang, "bannedForNoEnglishScript", $"{name}, {update.Message.From.Id}"),
                                update);
                    }
                    catch (Exception e)
                    {

                    }                    

                }
            }
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
