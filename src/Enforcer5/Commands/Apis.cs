using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Newtonsoft.Json;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static partial class Commands
    {
        public static void IsNSFWImage(long chatId, Message msg)
        {
            var watch = Redis.db.SetContainsAsync($"chat:{chatId}:watch", msg.From.Id).Result;
            if (watch) return;

            var usingNSFW = Redis.db.SetContainsAsync("bot:nsfwgroups", chatId).Result;
            if (usingNSFW)
            {
                var nsfwSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
                //var groupToken = "huPj6Tpc6zAjO8zFrnNVWrhlcEy4UV";
                var lang = Methods.GetGroupLanguage(msg, true).Doc;
                try
                {   
                    var groupToken = nsfwSettings.Where(e => e.Name.Equals("apikey")).FirstOrDefault().Value.ToString();
                    var expireTime = long.Parse(nsfwSettings.Where(e => e.Name.Equals("expireTime")).FirstOrDefault().Value.ToString());
                    int tryGenCount = 0;
                    if (System.DateTime.UtcNow.ToUnixTime() > expireTime && tryGenCount < 5 && expireTime != -1)
                    {
                        genNewToken(chatId);
                        nsfwSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
                        groupToken = nsfwSettings.Where(e => e.Name.Equals("apikey")).FirstOrDefault().Value;
                        expireTime = long.Parse(nsfwSettings.Where(e => e.Name.Equals("expireTime")).FirstOrDefault().Value);
                        tryGenCount++;
                    }
                    if (!string.IsNullOrEmpty(groupToken))
                    {
                        //var groupToken = "HsUVHtdIlaNZuuZbmgrbfwiykpfyyX";
                        var photo = msg.Photo.OrderByDescending(x => x.Height).FirstOrDefault(x => x.FileId != null);
                        var pathing = Bot.Api.GetFileAsync(photo.FileId).Result;
                        var photoURL = $"https://api.telegram.org/file/bot{Bot.TelegramAPIKey}/{pathing.FilePath}";
                        // var groupToken = Redis.db.StringGetAsync($"chat:{chatId}:clariToken");
                        using (var client = new HttpClient())
                        {
                            var url = "https://api.clarifai.com/v2/models/e9576d86d2004ed1a38ba0cf39ecb4b1/outputs";
                            var content = new StringContent(JsonConvert.SerializeObject(new ClarifaiInputs(photoURL)), Encoding.UTF8, "application/json");
                            client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {groupToken}");
                            var response = client.PostAsync(url, content).Result;
                            response.EnsureSuccessStatusCode();
                            var data = response.Content.ReadAsStringAsync().Result;
                            var result = JsonConvert.DeserializeObject<ClarifaiOutput>(data);
                            var chance = (double)(result.outputs[0].data.concepts.First(x => x.name == "nsfw").value * 100);
                            //Bot.SendReply(chance + $" Reponse time: {(DateTime.UtcNow - msg.Date):mm\\:ss\\.ff}", msg);
                            if (chance > 90.0)
                            {
                                var admins = nsfwSettings.Where(e => e.Name.Equals("adminAlert")).FirstOrDefault().Value;               
                                var action = nsfwSettings.Where(e => e.Name.Equals("action")).FirstOrDefault();
                                if (action.Value.Equals("ban"))
                                {
                                    var name = $"{msg.From.FirstName} [{ msg.From.Id}]";
                                    if (msg.From.Username != null) name = $"{name} (@{msg.From.Username})";
                                    var res = Methods.BanUser(chatId, msg.From.Id, lang);
                                    if (res)
                                    {
                                        Methods.SaveBan(msg.From.Id, "NSFWImage");
                                        Bot.SendReply(Methods.GetLocaleString(lang, "bannedfornsfwimage", $"Attention: {admins}: {name}", chance.ToString()), msg);
                                        Service.LogCommand(msg.Chat.Id, -1, "Enforcer", msg.Chat.Title, Methods.GetLocaleString(lang, "kickedfornsfwimage", $"Attention: {admins}: {name}", chance.ToString()), $"{msg.From.FirstName} ({msg.From.Id})");
                                    }
                                }
                                else
                                {
                                    var name = $"{msg.From.FirstName} [{ msg.From.Id}]";
                                    if (msg.From.Username != null) name = $"{name} (@{msg.From.Username}) ";
                                    Methods.KickUser(chatId, msg.From.Id, lang);
                                    Bot.SendReply(Methods.GetLocaleString(lang, "kickedfornsfwimage", $"Attention: {admins}: {name}", chance.ToString()), msg);
                                    Service.LogCommand(msg.Chat.Id, -1, "Enforcer", msg.Chat.Title, Methods.GetLocaleString(lang, "kickedfornsfwimage", $"Attention: {admins}: {name}", chance.ToString()), $"{msg.From.FirstName} ({msg.From.Id})");
                                }
                                Bot.DeleteMessage(chatId, msg.MessageId);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    if (e.Message.Contains("401"))
                    {
                        genNewToken(chatId, true);
                        return;
                    }
                    Console.WriteLine(e);
                    Methods.SendError(e.Message, msg, lang);
                }
            }
        }

        public static bool genNewToken(long chatId, bool sendOutput = false)
        {
            var data = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
            var appId = data.Where(e => e.Name.Equals("appId")).FirstOrDefault();
            var appSecret = data.Where(e => e.Name.Equals("appSecret")).FirstOrDefault();
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            try
            {
                using (var client = new HttpClient())
                {
                    var url = "https://api.clarifai.com/v2/token";
                    var content = new StringContent(JsonConvert.SerializeObject("\"grant_type\":\"client_credentials\""), Encoding.UTF8, "application/json");
                    String encoded = System.Convert.ToBase64String(System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(appId.Value + ":" + appSecret.Value));
                    client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Basic {encoded}");
                    var response = client.PostAsync(url, content).Result;
                    response.EnsureSuccessStatusCode();
                    var res = response.Content.ReadAsStringAsync().Result;
                    var result = JsonConvert.DeserializeObject<ClarifaiAPIKey>(res);

                    //var result = JsonConvert.DeserializeObject<ClarifaiOutput>(res);
                    try
                    {
                        Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "apikey", result.access_token);
                       var temp = Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "expireTime", System.DateTime.UtcNow.ToUnixTime() + (result.expires_in)).Result;
                       

                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        return false;
                    }
                    if (sendOutput)
                    {
                        Bot.Send(Methods.GetLocaleString(lang, "apikeysucgen"), chatId);
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Bot.Send(e.Message, chatId);
                return false;
            }



        }

        [Command(Trigger = "setnsfw", GroupAdminOnly = true, InGroupOnly = true)]
        public static void SetClient(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var appInfo = args[1].Split(' ');
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "appId", appInfo[0]);
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "appSecret", appInfo[1]);
                if (genNewToken(chatId, true))
                {
                    Redis.db.SetAddAsync("bot:nsfwgroups", chatId);
                    Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "activated", "on");
                    Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "action", "ban");
                    Bot.SendReply(Methods.GetLocaleString(lang, "nsfwapikeyset"), update);
                }
               
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "setnsfwapi", GroupAdminOnly = true, InGroupOnly = true)]
        public static void setclientapi(Update update, string[] args)
        {

            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "apikey", args[1].ToString());
                var temp = Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "expireTime", -1).Result;
                Redis.db.SetAddAsync("bot:nsfwgroups", chatId);
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "activated", "on");
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "action", "ban");
                Bot.SendReply(Methods.GetLocaleString(lang, "nsfwapikeyset"), update);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "delnsfw", GroupAdminOnly = true, InGroupOnly = true)]
        public static void DeleteClient(Update update, string[] args)
        {
            var usingNSFW = Redis.db.SetContainsAsync("bot:nsfwgroups", update.Message.Chat.Id).Result;
            if (usingNSFW)
            {
                Redis.db.SetRemoveAsync("bot:nsfwgroups", update.Message.Chat.Id);
            }
        }


        [Command(Trigger = "authnsfw", InGroupOnly = true, GlobalAdminOnly = true)]
        public static void autherisensfwgroup(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "autherised", "yes");
            Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "activated", "on");
            Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "action", "ban");
            Bot.SendReply("Activated: make sure api key is set", update);
        }
        [Command(Trigger = "deauthnsfw", InGroupOnly = true, GlobalAdminOnly = true)]
        public static void Deautherisensfwgroup(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "autherised", "no");
            Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "activated", "off");
            Bot.SendReply("Deactivated", update);
        }

        [Command(Trigger = "checknsfw", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void CheckNSFWSettings(Update update, string[] args)
        {
            long chatId = update.Message.Chat.Id;
            Message msg = update.Message;
            var nsfwSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
            //var groupToken = "huPj6Tpc6zAjO8zFrnNVWrhlcEy4UV";
            var lang = Methods.GetGroupLanguage(msg, true).Doc;
            try
            {
                var groupToken = nsfwSettings.Where(e => e.Name.Equals("apikey")).FirstOrDefault().Value.ToString();
                var expireTime = nsfwSettings.Where(e => e.Name.Equals("expireTime")).FirstOrDefault().Value.ToString();
                int tryGenCount = 0;
                if (!string.IsNullOrEmpty(groupToken))
                {
                    //var groupToken = "HsUVHtdIlaNZuuZbmgrbfwiykpfyyX";
                    var photo = msg.ReplyToMessage.Photo.OrderByDescending(x => x.Height).FirstOrDefault(x => x.FileId != null);
                    var pathing = Bot.Api.GetFileAsync(photo.FileId).Result;
                    var photoURL = $"https://api.telegram.org/file/bot{Bot.TelegramAPIKey}/{pathing.FilePath}";
                    // var groupToken = Redis.db.StringGetAsync($"chat:{chatId}:clariToken");
                    using (var client = new HttpClient())
                    {
                        var url = "https://api.clarifai.com/v2/models/e9576d86d2004ed1a38ba0cf39ecb4b1/outputs";
                        var content = new StringContent(JsonConvert.SerializeObject(new ClarifaiInputs(photoURL)), Encoding.UTF8, "application/json");
                        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {groupToken}");
                        var response = client.PostAsync(url, content).Result;
                        response.EnsureSuccessStatusCode();
                        var data = response.Content.ReadAsStringAsync().Result;
                        var result = JsonConvert.DeserializeObject<ClarifaiOutput>(data);
                        var chance = (double)(result.outputs[0].data.concepts.First(x => x.name == "nsfw").value * 100);
                        //Bot.SendReply(chance + $" Reponse time: {(DateTime.UtcNow - msg.Date):mm\\:ss\\.ff}", msg);
                        Bot.SendReply($"Image has a {chance}% change of being nsfw\nAPIKey: {groupToken}\nExpires at:{expireTime}\nExpires in:{(int.Parse(expireTime) - System.DateTime.UtcNow.ToUnixTime())}", msg);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Methods.SendError(e.Message, msg, lang);
            }
        }
    }
}
