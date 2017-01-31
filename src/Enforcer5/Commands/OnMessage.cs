﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    public static class OnMessage
    {
        public static async Task OnChatMessage(Update update)
        {            
            var msgType = Methods.GetMediaType(update.Message);
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!isIgnored(chatId, msgType))
            {
                var msgs = Redis.db.StringGetAsync($"spam:{chatId}:{update.Message.From.Id}").Result;
                var num = msgs.IsInteger ? int.Parse(msgs) : 0;
                if (num == 0) num = 1;
                var maxSpam = 8;
                if (update.Message.Chat.Type == ChatType.Private) maxSpam = 12;
                var floodSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:flood").Result;
                var maxMsgs = floodSettings.Where(e => e.Name.Equals("MaxFlood")).FirstOrDefault();
                var maxTime = new TimeSpan(0,0,0,5);               
                if ((DateTime.Now.ToUnixTime() - update.Message.Date.ToUnixTime()) < 30)
                {       
                    await Redis.db.StringSetAsync($"spam:{chatId}:{update.Message.From.Id}", num + 1, maxTime);   
                }
                if (num == int.Parse(maxMsgs.Value) + 1)
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
                                Methods.GetLocaleString(lang, "bannedForFlood", ""));
                        }
                        else
                        {
                            await Methods.KickUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "flood");
                        }
                        if (msgs == (int.Parse(maxMsgs.Value) + 1) || msgs == int.Parse(maxMsgs.Value) + 5)
                        {
                            await Bot.Send(
                                Methods.GetLocaleString(lang, "bannedForFlood", $"{update.Message.From.FirstName}, {update.Message.From.Id}"),
                                update);
                        }
                    }
                    catch (Exception e)
                    {

                    }
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
                bool check = false;
                if (text.Contains(rtl)) check = true;
                if (name.Contains(rtl)) check = true;
                if (lastName.Contains(rtl)) check = true;
                try
                {
                    if (check)
                    {
                        if (status.Equals("kick"))
                        {
                            await Methods.KickUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "rtl");
                        }
                        else
                        {
                            await Methods.BanUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "rtl");
                            Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                Methods.GetLocaleString(lang, "bannedForRtl", ""));
                        }
                        await Bot.Send(
                                Methods.GetLocaleString(lang, "bannedForRtl", $"{name}, {update.Message.From.Id}"),
                                update);
                    }
                }
                catch (Exception e)
                {
                    
                }

            }

            var encode = Encoding.ASCII.GetBytes(update.Message.Text);
            var res = encode.Where(
                e =>
                    (int.Parse(e.ToString()) >= 216 && int.Parse(e.ToString()) <= 219) ||
                    (int.Parse(e.ToString()) >= 128 && int.Parse(e.ToString()) <= 191)).FirstOrDefault();
            if (!string.IsNullOrEmpty(res.ToString()))
            {
                var arabStatus = Redis.db.HashGetAsync($"chat:{chatId}:char", "Arab").Result.ToString();
                if (!string.IsNullOrEmpty(arabStatus)) arabStatus = "allowed";
                if (arabStatus.Equals("kick") || arabStatus.Equals("ban"))
                {
                    var name = update.Message.From.FirstName;
                    var rtl = "‮";
                    var lastName = "x";
                    if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
                    if (update.Message.From.LastName != null) lastName = update.Message.From.LastName;
                    try
                    {
                        if (status.Equals("kick"))
                        {
                            await Methods.KickUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "arab");
                        }
                        else
                        {
                            await Methods.BanUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "rtl");
                            Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                Methods.GetLocaleString(lang, "bannedForNoEnglishScript", ""));
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
            var banDetails = Redis.db.HashGetAllAsync($"globanBan:{update.Message.From.Id}").Result;
            var isBanned = banDetails.Where(e => e.Name.Equals("banned")).FirstOrDefault();
            var seenSupport = banDetails.Where(e => e.Name.Equals("seen")).FirstOrDefault();
            if (isBanned.Value.Equals("1"))
            {
                if (update.Message.Chat.Id != Constants.SupportId)
                {
                    try
                    {
                        await Methods.BanUser(chatId, update.Message.From.Id, lang);
                        var motivation = banDetails.Where(e => e.Name.Equals("motivation")).FirstOrDefault();
                        Methods.AddBanList(chatId, update.Message.From.Id, $"{update.Message.From.FirstName} ({update.Message.From.Id})", $"Global banned for: {motivation}");
                        await Bot.Send(
                            Methods.GetLocaleString(lang, "globalBanNotif",
                                $"{update.Message.From.FirstName}, {update.Message.From.Id}", motivation), update);                        
                    }
                    catch (Exception e)
                    {
                        
                    }
                    await Bot.Send(
                            $"{update.Message.From.FirstName}, {update.Message.From.Id} has been notified of ban in {chatId} {update.Message.Chat.Title}",
                            Constants.Devs[0]);
                }
            }
            else if (seenSupport.Name.Equals("1"))
            {
                var motivation = banDetails.Where(e => e.Name.Equals("motivation")).FirstOrDefault();
                await Redis.db.HashSetAsync($"globalBan:{update.Message.From.Id}", "seen", 1);
                await Bot.Send(
                    $"{update.Message.From.FirstName} has a history of {motivation} and has joined @werewolfsupport to appeal there global ban", update);
            }
        }

        public static bool isIgnored(long chatId, string msgType)
        {
            var status = Redis.db.HashGetAsync($"chat:{chatId}:floodexceptions", msgType).Result;
            return status.Equals("yes");
        }
    }
}
