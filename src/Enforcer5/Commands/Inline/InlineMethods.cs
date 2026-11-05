using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static class InlineMethods
    {
        
        /// <summary>
        /// The asking user's own running tempbans, in either edition, filtered by <paramref name="args"/> (part of
        /// the group's title or the id). Names and titles are raw: the result's title and description are plain
        /// text, and the message escapes them. Until 2026-11-04 this never found anything (a string id compared
        /// with a number, issue #30), read the schedules itself and took two hours off the end.
        /// </summary>
        internal static TempbanUser[] GetTempbanUserDetails(User user, string args, XDocument lang)
        {
            var now = Clock.UtcNow;
            var name = Redis.db.HashGetAsync($"user:{user.Id}", "name").Result;
            var shownName = name.HasValue ? name.ToString() : user.FirstName;
            var results = new List<TempbanUser>();
            var seen = new HashSet<long>();
            foreach (var repository in Repositories.TempbansOfBothEditions)
            {
                IReadOnlyList<UserTempban> tempbans;
                try
                {
                    tempbans = repository.OfUserAsync(user.Id, now).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading the tempbans of {user.Id} for the inline query");
                    continue;
                }
                foreach (var tempban in tempbans)
                {
                    if (!seen.Add(tempban.ChatId)) continue;
                    var title = Service.GetCachedGroupTitle(tempban.ChatId);
                    var groupName = string.IsNullOrEmpty(title) ? tempban.ChatId.ToString() : title;
                    if (!string.IsNullOrEmpty(args) &&
                        !groupName.Contains(args, StringComparison.OrdinalIgnoreCase) &&
                        !(shownName ?? "").Contains(args, StringComparison.OrdinalIgnoreCase) &&
                        !tempban.ChatId.ToString().Contains(args) && !user.Id.ToString().Contains(args))
                        continue;
                    results.Add(new TempbanUser
                    {
                        userId = user.Id.ToString(),
                        name = shownName,
                        groupName = groupName,
                        unbanTime = $"{tempban.UntilUtc:HH:mm:ss dd-MM-yyyy} {Methods.GetLocaleString(lang, "uct")}",
                        groupId = tempban.ChatId.ToString()
                    });
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
                            // Not an error yet: fall back to the error chat's language.
                            try
                            {
                                lang = Methods.GetGroupLanguage(Bot.ErrorChatId).Doc;
                                text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                                results.Add(new HelpArticle()
                                {
                                    details = text,
                                    name = request
                                });
                            }
                            catch (Exception ep)
                            {
                                // The entry is left out of the results, but help text should exist for it.
                                ErrorReporter.Report(ep, $"Inline help text for {request}");
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
                            // Not an error yet: fall back to the error chat's language.
                            try
                            {
                                lang = Methods.GetGroupLanguage(Bot.ErrorChatId).Doc;
                                text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                                results.Add(new HelpArticle()
                                {
                                    details = text,
                                    name = request
                                });
                            }
                            catch (Exception ep)
                            {
                                // The entry is left out of the results, but help text should exist for it.
                                ErrorReporter.Report(ep, $"Inline help text for {request}");
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
