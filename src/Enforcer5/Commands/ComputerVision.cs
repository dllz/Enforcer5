using System;
using System.Collections.Generic;
using System.Linq;
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
        public static async Task IsNSFWImage(long chatId, Message msg)
        {
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
                            Bot.SendReply(chance + $" Reponse time: {(DateTime.UtcNow - msg.Date):mm\\:ss\\.ff}", msg);
                            if (chance > 95.0)
                            {                              
                                var action = nsfwSettings.Where(e => e.Name.Equals("action")).FirstOrDefault();
                                if (action.Value.Equals("ban"))
                                {
                                    var name = $"{msg.From.FirstName} [{ msg.From.Id}]";
                                    if (msg.From.Username != null) name = $"{name} (@{msg.From.Username})";
                                    await Methods.BanUser(chatId, msg.From.Id, lang);
                                    Methods.SaveBan(msg.From.Id, "NSFWImage");
                                    await Bot.SendReply(Methods.GetLocaleString(lang, "bannedfornsfwimage", name, chance.ToString()), msg);
                                }
                                else
                                {
                                    var name = $"{msg.From.FirstName} [{ msg.From.Id}]";
                                    if (msg.From.Username != null) name = $"{name} (@{msg.From.Username}) ";
                                    await Methods.KickUser(chatId, msg.From.Id, lang);
                                    await Bot.SendReply(Methods.GetLocaleString(lang, "kickedfornsfwimage", name, chance.ToString()), msg);
                                }
                            }
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

        [Command(Trigger = "authnsfw", InGroupOnly = true, GlobalAdminOnly = true)]
        public static async Task autherisensfwgroup(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            await Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "autherised", "yes");
            await Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "activated", "on");
            await Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "action", "ban");
            await Bot.SendReply("Activated: make sure api key is set", update);
        }
        [Command(Trigger = "deauthnsfw", InGroupOnly = true, GlobalAdminOnly = true)]
        public static async Task Deautherisensfwgroup(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            await Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "autherised", "no");
            await Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "activated", "off");
            await Bot.SendReply("Deactivated", update);
        }
        [Command(Trigger = "setApiKey", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task SetNSFWApiKey(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "apikey", args[1]);
                await Bot.SendReply("API key updated", update);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Methods.SendError(e.Message, update.Message, lang);
            }
        }
    }
}
