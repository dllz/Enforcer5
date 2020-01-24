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
using Clarifai.API;

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
                    /*  var expireTime = long.Parse(nsfwSettings.Where(e => e.Name.Equals("expireTime")).FirstOrDefault().Value.ToString());
                      int tryGenCount = 0;
                      if (System.DateTime.UtcNow.ToUnixTime() > expireTime && tryGenCount < 5 && expireTime != -1)
                      {
                          genNewToken(chatId);
                          nsfwSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
                          groupToken = nsfwSettings.Where(e => e.Name.Equals("apikey")).FirstOrDefault().Value;
                          expireTime = long.Parse(nsfwSettings.Where(e => e.Name.Equals("expireTime")).FirstOrDefault().Value);
                          tryGenCount++;
                      }*/
                    if (!string.IsNullOrEmpty(groupToken))
                    {
                        //var groupToken = "HsUVHtdIlaNZuuZbmgrbfwiykpfyyX";
                        var photo = msg.Photo.OrderByDescending(x => x.Height).FirstOrDefault(x => x.FileId != null);
                        var pathing = Bot.Api.GetFileAsync(photo.FileId).Result;
                        var photoURL = $"https://api.telegram.org/file/bot{Bot.TelegramAPIKey}/{pathing.FilePath}";
                        // var groupToken = Redis.db.StringGetAsync($"chat:{chatId}:clariToken");
                        var clarifaiClient = new ClarifaiClient(groupToken);
                        var request = clarifaiClient.PublicModels.NsfwModel.Predict(
                            new Clarifai.DTOs.Inputs.ClarifaiURLImage(photoURL));
                        Clarifai.API.Responses.ClarifaiResponse<Clarifai.DTOs.Models.Outputs.ClarifaiOutput<Clarifai.DTOs.Predictions.Concept>> response = request.ExecuteAsync().Result;                    
                        if (response.IsSuccessful)
                        {
                            var chance = ((double)response.Get().Data.First(x => x.Name == "nsfw").Value) * 100;

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
                    
                    Console.WriteLine(e);
                    Methods.SendError(e.Message, msg, lang);
                }
            }
        }

        public static void IsNSFWVideo(long chatId, Message msg)
        {
            var watch = Redis.db.SetContainsAsync($"chat:{chatId}:watch", msg.From.Id).Result;
            if (watch) return;

            var usingNSFW = Redis.db.SetContainsAsync("bot:nsfwgroups", chatId).Result;
            if (usingNSFW)
            {
                var nsfwSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
                var lang = Methods.GetGroupLanguage(msg, true).Doc;
                try
                {
                    var groupToken = nsfwSettings.Where(e => e.Name.Equals("apikey")).FirstOrDefault().Value.ToString();
                  
                    if (!string.IsNullOrEmpty(groupToken))
                    {
                        var video = msg.Video.FileId;
                        if(video != null)
                        {
                            var pathing = Bot.Api.GetFileAsync(video).Result;
                            var videoURL = $"https://api.telegram.org/file/bot{Bot.TelegramAPIKey}/{pathing.FilePath}";
                            var clarifaiClient = new ClarifaiClient(groupToken);
                            var request = clarifaiClient.PublicModels.NsfwVideoModel.Predict(
                                new Clarifai.DTOs.Inputs.ClarifaiURLVideo(videoURL));
                            Clarifai.API.Responses.ClarifaiResponse<Clarifai.DTOs.Models.Outputs.ClarifaiOutput<Clarifai.DTOs.Predictions.Frame>> response = request.ExecuteAsync().Result;
                            if (response.IsSuccessful)
                            {
                                var chance = 0.0;
                                var foundFrame = false;
                                foreach (var item in response.Get().Data)
                                {
                                    if(foundFrame == false)
                                    {
                                        foreach (var concept in item.Concepts)
                                        {
                                            if (concept.Name.Equals("nsfw"))
                                            {
                                                chance = (double)concept.Value * 100;
                                                if (chance > 90)
                                                {
                                                    foundFrame = true;
                                                    break;
                                                }
                                            }
                                        }
                                    }else
                                    {
                                        break;
                                    }
                                }                              

                                if (foundFrame)
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
                }
                catch (Exception e)
                {

                    Console.WriteLine(e);
                    Methods.SendError(e.Message, msg, lang);
                }
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
                Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", "apikey", appInfo[0]);
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


   /*     [Command(Trigger = "authnsfw", InGroupOnly = true, GlobalAdminOnly = true)]
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
        }*/

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
                    switch (update.Message.Type)
                    {
                        case Telegram.Bot.Types.Enums.MessageType.VideoMessage:
                            //var groupToken = "HsUVHtdIlaNZuuZbmgrbfwiykpfyyX";
                            var video = msg.ReplyToMessage.Video;
                            var pathing = Bot.Api.GetFileAsync(video.FileId).Result;
                            var videoURL = $"https://api.telegram.org/file/bot{Bot.TelegramAPIKey}/{pathing.FilePath}";
                            // var groupToken = Redis.db.StringGetAsync($"chat:{chatId}:clariToken");
                            // var groupToken = Redis.db.StringGetAsync($"chat:{chatId}:clariToken");
                            var clarifaiClient = new ClarifaiClient(groupToken);
                            var request = clarifaiClient.PublicModels.NsfwVideoModel.Predict(
                                new Clarifai.DTOs.Inputs.ClarifaiURLVideo(videoURL));
                            Clarifai.API.Responses.ClarifaiResponse<Clarifai.DTOs.Models.Outputs.ClarifaiOutput<Clarifai.DTOs.Predictions.Frame>> response = request.ExecuteAsync().Result;
                            if (response.IsSuccessful)
                            {
                                var chance = 0.0;
                                var foundFrame = false;
                                foreach (var item in response.Get().Data)
                                {
                                    if (foundFrame == false)
                                    {
                                        foreach (var concept in item.Concepts)
                                        {
                                            if (concept.Name.Equals("nsfw"))
                                            {
                                                chance = (double)concept.Value * 100;
                                                if (chance > 90)
                                                {
                                                    foundFrame = true;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }

                                Bot.SendReply($"Image has a {chance}% change of being nsfw\nAPIKey: {groupToken}\nExpires at:{expireTime}\nExpires in:{(int.Parse(expireTime) - System.DateTime.UtcNow.ToUnixTime())}", msg);
                            }
                            else
                            {
                                Bot.SendReply("Shit didnt work", msg);
                            }
                            break;
                        case Telegram.Bot.Types.Enums.MessageType.PhotoMessage:
                            //var groupToken = "HsUVHtdIlaNZuuZbmgrbfwiykpfyyX";
                            var photo = msg.ReplyToMessage.Photo.OrderByDescending(x => x.Height).FirstOrDefault(x => x.FileId != null);
                            pathing = Bot.Api.GetFileAsync(photo.FileId).Result;
                           var photoURL = $"https://api.telegram.org/file/bot{Bot.TelegramAPIKey}/{pathing.FilePath}";
                            // var groupToken = Redis.db.StringGetAsync($"chat:{chatId}:clariToken");
                            // var groupToken = Redis.db.StringGetAsync($"chat:{chatId}:clariToken");
                            clarifaiClient = new ClarifaiClient(groupToken);
                            var photoRequest = clarifaiClient.PublicModels.NsfwModel.Predict(
                                new Clarifai.DTOs.Inputs.ClarifaiURLImage(photoURL));
                            Clarifai.API.Responses.ClarifaiResponse<Clarifai.DTOs.Models.Outputs.ClarifaiOutput<Clarifai.DTOs.Predictions.Concept>> photoResponse = photoRequest.ExecuteAsync().Result;
                            /*  var url = "https://api.clarifai.com/v2/models/e9576d86d2004ed1a38ba0cf39ecb4b1/outputs";
                             var content = new StringContent(JsonConvert.SerializeObject(new ClarifaiInputs(photoURL)), Encoding.UTF8, "application/json");
                              client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {groupToken}");
                              var response = client.PostAsync(url, content).Result;
                              response.EnsureSuccessStatusCode();
                              var data = response.Content.ReadAsStringAsync().Result;
                              var result = JsonConvert.DeserializeObject<ClarifaiOutput>(data);*/
                            if (photoResponse.IsSuccessful)
                            {
                                var chance = ((double)photoResponse.Get().Data.First(x => x.Name == "nsfw").Value) * 100;

                                Bot.SendReply($"Image has a {chance}% change of being nsfw\nAPIKey: {groupToken}\nExpires at:{expireTime}\nExpires in:{(int.Parse(expireTime) - System.DateTime.UtcNow.ToUnixTime())}", msg);
                            }else
                            {
                                Bot.SendReply("Shit didnt work", msg);
                            }
                            break;
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
