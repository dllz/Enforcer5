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
                results = bitBan.Select(x => new TempbanUser()
                {
                    userId = $"{x.Value.ToString().Split(':')[1]}",
                    name = Redis.db.HashGetAsync($"user:{x.Value.ToString().Split(':')[1]}", "name").Result.ToString()
                        .FormatHTML(),
                    groupName = Redis.db.HashGetAsync($"chat:{x.Value.ToString().Split(':')[0]}:details", "name").Result
                        .ToString()
                        .FormatHTML(),
                    unbanTime =
                        $"{long.Parse(x.Name).FromUnixTime().AddHours(-2).ToString("hh:mm:ss dd-MM-yyyy")} {Methods.GetLocaleString(lang, "uct")}"
                }).Where(x => x.name.Contains(args) || x.groupName.Contains(args) || x.userId.Contains(args)).ToList();
            }
            else
            {
                results = bitBan.Take(10).Select(x => new TempbanUser()
                {
                    userId = $"{x.Value.ToString().Split(':')[1]}",                    
                    name = Redis.db.HashGetAsync($"user:{x.Value.ToString().Split(':')[1]}", "name").Result.ToString()
                        .FormatHTML(),
                    groupName = Redis.db.HashGetAsync($"chat:{x.Value.ToString().Split(':')[0]}:details", "name").Result
                        .ToString()
                        .FormatHTML(),
                    unbanTime =
                        $"{long.Parse(x.Name).FromUnixTime().AddHours(-2).ToString("hh:mm:ss dd-MM-yyyy")} {Methods.GetLocaleString(lang, "uct")}"
                }).ToList();
            }
            if (results.Count == 0)
            {
                results.Add(new TempbanUser()
                {
                    name = $"Nothing Found",
                    unbanTime = "",
                    groupName = ""
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
            return results.ToArray();
        }
           
        
    }
}
