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
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static partial class Commands
    {
        public static void IsNSFWImage(long chatId, Message msg)
        {
            var watch = Redis.db.SetContainsAsync($"chat:{chatId}:watch", msg.From.Id).Result;
            if (watch) return;
            var nsfwSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
            var auth = nsfwSettings.Where(e => e.Name.Equals("autherised")).FirstOrDefault();
            var on = nsfwSettings.Where(e => e.Name.Equals("activated")).FirstOrDefault();
            if (auth.Value.Equals("yes") && on.Value.Equals("on"))
            {
                //var groupToken = "huPj6Tpc6zAjO8zFrnNVWrhlcEy4UV";
                var lang = Methods.GetGroupLanguage(msg).Doc;
                try
                {
                    var groupToken = nsfwSettings.Where(e => e.Name.Equals("apikey")).FirstOrDefault().Value;
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
                                    Methods.BanUser(chatId, msg.From.Id, lang);
                                    Methods.SaveBan(msg.From.Id, "NSFWImage");
                                    Bot.SendReply(Methods.GetLocaleString(lang, "bannedfornsfwimage", $"Attention: {admins}: {name}", chance.ToString()), msg);
                                }
                                else
                                {
                                    var name = $"{msg.From.FirstName} [{ msg.From.Id}]";
                                    if (msg.From.Username != null) name = $"{name} (@{msg.From.Username}) ";
                                    Methods.KickUser(chatId, msg.From.Id, lang);
                                    Bot.SendReply(Methods.GetLocaleString(lang, "kickedfornsfwimage", $"Attention: {admins}: {name}", chance.ToString()), msg);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    if (e.Message.Contains("401"))
                    {
                        genNewToken(chatId);
                        Bot.SendReply("Your api key expired, a new one has been auto generated", msg);
                    }
                    Console.WriteLine(e);
                    Methods.SendError(e.Message, msg, lang);
                }
            }
        }

        public static void genNewToken(long chatId, bool sendOutput = false)
        {
            var data = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
            var appId = data.Where(e => e.Name.Equals("appId")).FirstOrDefault();
            var appSecret = data.Where(e => e.Name.Equals("appSecret")).FirstOrDefault();

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
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                    if (sendOutput)
                    {
                        Bot.Send("New Api generated", chatId);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Bot.Send(e.Message, chatId);
            }



        }

        [Command(Trigger = "genapi", GroupAdminOnly = true, InGroupOnly = true)]
        public static void GenerateNewApi(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                genNewToken(update.Message.Chat.Id, true);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "setAppId", GroupAdminOnly = true, InGroupOnly = true)]
        public static void SetClient(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "appId", args[1]);
                Bot.SendReply("App ID key updated", update);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "setSecret", GroupAdminOnly = true, InGroupOnly = true)]
        public static void SetSecret(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "appSecret", args[1]);
                Bot.SendReply("App secret key updated", update);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Methods.SendError(e.Message, update.Message, lang);
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

        [Command(Trigger = "nsfwalert", InGroupOnly = true, GroupAdminOnly = true)]
        public static void NSFWAdminAlert(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "adminAlert", args[1]);
                Bot.SendReply("Admin alert updated", update);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Methods.SendError(e.Message, update.Message, lang);
            }
        }
    }
}
