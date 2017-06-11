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
                var tempbans = bitBan.ToImmutableHashSet();
                foreach (var mem in tempbans)
                {
                    var subStrings = mem.Value.ToString().Split(':');
                    var chatId = long.Parse(subStrings[0]);
                    var userId = int.Parse(subStrings[1]);
                    var name = Redis.db.HashGetAsync($"user:{userId}", "name").Result.ToString().FormatHTML();
                    var groupName = Redis.db.HashGetAsync($"chat:{chatId}:details", "name").Result.ToString()
                        .FormatHTML();
                    if (name.Contains(args) || userId.ToString().Contains(args) || groupName.Contains(args) ||
                        chatId.ToString().Contains(args))
                    {
                        results.Add(new TempbanUser()
                        {
                            name = $"{name} ({userId})",
                            unbanTime =
                                $"{long.Parse(mem.Name).FromUnixTime().AddHours(-2).ToString("hh:mm:ss dd-MM-yyyy")} {Methods.GetLocaleString(lang, "uct")}",
                            group = $"{groupName}"
                        });
                    }
                }
            }
            else
            {
                var tempbans = bitBan.Take(10).ToImmutableHashSet();
                foreach (var mem in tempbans)
                {
                    var subStrings = mem.Value.ToString().Split(':');
                    var chatId = long.Parse(subStrings[0]);
                    var userId = int.Parse(subStrings[1]);
                    var name = Redis.db.HashGetAsync($"user:{userId}", "name").Result.ToString().FormatHTML();
                    var groupName = Redis.db.HashGetAsync($"chat:{chatId}:details", "name").Result.ToString()
                        .FormatHTML();
                    results.Add(new TempbanUser()
                    {
                        name = $"{name} ({userId})",
                        unbanTime =
                            $"{long.Parse(mem.Name).FromUnixTime().AddHours(-2).ToString("hh:mm:ss dd-MM-yyyy")} {Methods.GetLocaleString(lang, "uct")}",
                        group = $"{groupName}"
                    });
                }
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
