using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using Enforcer5.Storage.PrincipalContent;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static class InlineMethods
    {
        
        internal static TempbanUser[] GetTempbanUserDetails(User user, string args, XDocument lang)
        {
            // This lists every group in which the user is tempbanned, and an inline query carries no
            // group context, so it is the one user-facing feature that has to read both stores while
            // the population is mixed. The two sets are disjoint by group state - Redis entries for
            // groups PostgreSQL owns are filtered out below - so a union is correct and no
            // aggregation is involved.
            var postgres = ReadPostgresTempbans(user, args);
            if (StorageRouting.GlobalTierPostgresIsPrimary && postgres.Length > 0)
                return postgres;

            var redis = ReadRedisTempbans(user, args, lang);
            var merged = redis.Concat(postgres).ToArray();
            return merged.Length == 0
                ? new[] { new TempbanUser { name = "Nothing Found", unbanTime = "", groupName = "", groupId = "", userId = "" } }
                : merged;
        }

        private static TempbanUser[] ReadPostgresTempbans(User user, string args)
        {
            if (string.IsNullOrWhiteSpace(Helpers.RegHelper.GetRegValue("PostgresConnection")))
                return Array.Empty<TempbanUser>();
            try
            {
                var sanctions = OperationsStore.ListActiveSanctionsAsync(SanctionKinds.TempBan).Result;
                return sanctions.Where(value => value.UserId == user.Id).Select(value =>
                {
                    var userName = PrincipalContentStore.GetUserAsync(value.UserId).Result?.Name ?? string.Empty;
                    return new TempbanUser
                    {
                        userId = value.UserId.ToString(), name = userName.FormatHTML(),
                        groupName = value.GroupId.ToString(), groupId = value.GroupId.ToString(),
                        unbanTime = value.ExpiresAt?.UtcDateTime.ToString("hh:mm:ss dd-MM-yyyy") ?? string.Empty
                    };
                }).Where(value => string.IsNullOrEmpty(args) || value.name.Contains(args,
                    StringComparison.OrdinalIgnoreCase) || value.groupId.Contains(args)).ToArray();
            }
            catch (Exception e)
            {
                LogHelper.Error($"Inline tempban lookup against PostgreSQL failed: {e.Message}");
                return Array.Empty<TempbanUser>();
            }
        }

        private static TempbanUser[] ReadRedisTempbans(User user, string args, XDocument lang)
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
            // Entries for migrated groups stay in the global hash for the migration to move, so
            // they are filtered here rather than reported twice.
            return results
                .Where(value => !long.TryParse(value.groupId, out var chatId) ||
                                !StorageRouting.GroupPostgresIsPrimary(chatId))
                .ToArray();
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
