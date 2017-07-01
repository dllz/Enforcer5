using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static class InlineMethods
    {
        
        internal static TempbanUser[] GetTempbanUserDetails(User user, string args, XDocument lang)
        {
            var bitBan = Redis.db.HashGetAllAsync("tempbanned").Result.ToList();
            bitBan.AddRange(Redis.db.HashGetAllAsync("tempbannedPremium").Result);
            
            var results = new List<TempbanUser>();            
            if (args != null)
            {
                string userId, chatId, name = "", groupname = "";
                foreach (var x in bitBan)
                {
                    userId = x.Value.ToString().Split(':')[1];
                    chatId = x.Value.ToString().Split(':')[0];                    
                    if (userId.Equals(user.Id))
                    {
                        var tempname = Redis.db.HashGetAsync($"user:{userId}", "name").Result;
                        if (tempname.HasValue)
                            name = tempname.ToString().FormatHTML();
                        var tempgroupname = Redis.db.HashGetAsync($"chat:{chatId}:details", "name").Result;
                        if (tempgroupname.HasValue)
                        {
                            groupname = tempgroupname.ToString().FormatHTML();
                        }
                        if (name.ToLower().Contains(args.ToLower()) | groupname.ToLower().Contains(args.ToLower()) | userId.Contains(args))
                        {
                            results.Add(new TempbanUser()
                            {
                                userId = userId,
                                name = name,
                                groupName = groupname,
                                unbanTime =
                                    $"{long.Parse(x.Name).FromUnixTime().AddHours(-2).ToString("hh:mm:ss dd-MM-yyyy")} {Methods.GetLocaleString(lang, "uct")}",
                                groupId = chatId
                            });
                        }                      
                    }
                }
            }
            else
            {
                string userId, chatId, groupname = "Unknown Group Name";
                foreach (var x in bitBan)
                {
                    userId = x.Value.ToString().Split(':')[1];
                    chatId = x.Value.ToString().Split(':')[0];
                    if (userId.Equals(user.Id))
                    {
                        var tempgroupname = Redis.db.HashGetAsync($"chat:{chatId}:details", "name").Result;
                        if (tempgroupname.HasValue)
                        {
                            groupname = tempgroupname.ToString().FormatHTML();
                        }
                        results.Add(new TempbanUser()
                        {
                            userId = userId,
                            name = Redis.db.HashGetAsync($"user:{userId}", "name").Result.ToString()
                                .FormatHTML(),
                            groupName = groupname,
                            unbanTime =
                                $"{long.Parse(x.Name).FromUnixTime().AddHours(-2).ToString("hh:mm:ss dd-MM-yyyy")} {Methods.GetLocaleString(lang, "uct")}",
                            groupId = chatId
                        });
                    }
                }
            }
            if (results.Count == 0)
            {
                results.Add(new TempbanUser()
                {
                    name = $"Nothing Found",
                    unbanTime = "",
                    groupName = "",
                    groupId = "",
                    userId = ""
                });
            }
            return results.ToArray();
        }

        internal static HelpArticle[] GetHelpArticles(string args, XDocument lang)
        {           
            var request = "";
            var results = new List<HelpArticle>();
            var triggerList = Bot.Commands.Select(e => e.Trigger.ToLower()).ToList();
            var extras = Methods.GetLocaleString(lang, $"otherhelpList").ToLower().Split(':').ToList();
            triggerList.AddRange(extras);
            if (!string.IsNullOrEmpty(args))
            {                
                foreach (var mem in triggerList)
                {
                    if (mem.Contains(args.ToLower()))
                    {
                        request = mem;                        
                        string text;
                        try
                        {
                            text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                            results.Add(new HelpArticle()
                            {
                                details = text,
                                name = request
                            });
                        }
                        catch (Exception e)
                        {
                            try
                            {
                                lang = Methods.GetGroupLanguage(-1001076212715).Doc;
                                text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                                results.Add(new HelpArticle()
                                {
                                    details = text,
                                    name = request
                                });
                            }
                            catch (Exception ep)
                            {

                            }
                        }

                    }
                }
            }
            else
            {
                foreach (var mem in triggerList)
                {
                        request = mem;
                        string text;
                        try
                        {
                            text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                            results.Add(new HelpArticle()
                            {
                                details = text,
                                name = request
                            });
                        }
                        catch (Exception e)
                        {
                            try
                            {
                                lang = Methods.GetGroupLanguage(-1001076212715).Doc;
                                text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                                results.Add(new HelpArticle()
                                {
                                    details = text,
                                    name = request
                                });
                            }
                            catch (Exception ep)
                            {

                            }
                        }

                    }
                }
            if (results.Count == 0)
            {
                results.Add(new HelpArticle()
                {
                    name = $"Nothing Found",
                    details = ""
                });
            }
            return results.ToArray();
        }
           
        
    }
}
