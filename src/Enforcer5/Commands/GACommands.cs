using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Handlers;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot;
#pragma warning disable 4014
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "missinghcommands", UploadAdmin = true)]
        public static void MissingHcommands(Update update, string[] args)
        {
            var commands = Bot.Commands.Select(e => e.Trigger.ToLower()).ToList();
            var helplist = Methods.GetHelpList(Methods.GetGroupLanguage(Bot.ErrorChatId).Doc).Split('\n').Select(e => e.ToLower()).ToList();

            string missing = "<b>Missing hcommand strings:</b>\n";

            foreach (var s in commands.Where(x => !helplist.Contains(x)))
            {
                missing += s + "\n";
            }

            Bot.SendReply(missing, update.Message);
        }

        [Command(Trigger = "uploadlanguage", UploadAdmin = true)]
        public static void UploadLang(Update update, string[] args)
        {
            // An .xml or a .zip of them; a language admin then decides in the Review menu (LanguageUpload/).
            var message = update.Message;
            Bot.Dispatch(() => LanguageUpload.LanguageUploads.StartAsync(message));
        }

        [Command(Trigger = "validatelanguages", Menu = Attributes.CommandMenu.Hidden)]
        public static void ValidateLangs(Update update, string[] args)
        {
            if (!Methods.IsLangAdmin(update.Message.From.Id) && update.Message.Chat.Id != Constants.TranslatorsId) return;

            var langs = Program.LangaugeList;


            List<InlineKeyboardButton> buttons =
                langs.Select(x => x.Base)
                    .Distinct()
                    .OrderBy(x => x)
                    .Select(x => new InlineKeyboardButton(x, $"validate:{update.Message.From.Id}:{x}:null:base"))
                    .ToList();
            //buttons.Insert(0, new InlineKeyboardButton("All", $"validate|{update.Message.From.Id}|All|null|base"));

            var baseMenu = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < buttons.Count; i++)
            {
                if (buttons.Count - 1 == i)
                {
                    baseMenu.Add(new[] {buttons[i]});
                }
                else
                    baseMenu.Add(new[] {buttons[i], buttons[i + 1]});
                i++;
            }

            var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
            try
            {
                 Bot.Api.SendMessage(update.Message.Chat.Id, "Validate which language?",
                    replyParameters: new ReplyParameters { MessageId = update.Message.MessageId }, replyMarkup: menu);
            }
            catch (AggregateException e)
            {
                ErrorReporter.Report(e, "Sending language menu");
                foreach (var ex in e.InnerExceptions)
                {
                    var x = ex as ApiRequestException;

                     Bot.Send(x.Message, update.Message.Chat.Id);
                }
            }
            catch (ApiRequestException ex)
            {
                ErrorReporter.Report(ex, "Sending language menu");
                 Bot.Send(ex.Message, update.Message.Chat.Id);
            }
        }

        [Command(Trigger = "getlanguage", Menu = Attributes.CommandMenu.Hidden)]
        public static void GetLang(Update update, string[] args)
        {
            if (!Methods.IsLangAdmin(update.Message.From.Id) && update.Message.Chat.Id != Constants.TranslatorsId) return;

            var langs = Program.LangaugeList;


            List<InlineKeyboardButton> buttons = langs.Select(x => x.Base).Distinct().OrderBy(x => x).Select(x => new InlineKeyboardButton(x, $"getlang:{update.Message.From.Id}:{x}:null:base")).ToList();
            buttons.Insert(0, new InlineKeyboardButton("All", $"getlang:{update.Message.From.Id}:All:null:base"));

            var baseMenu = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < buttons.Count; i++)
            {
                if (buttons.Count - 1 == i)
                {
                    baseMenu.Add(new[] { buttons[i] });
                }
                else
                    baseMenu.Add(new[] { buttons[i], buttons[i + 1] });
                i++;
            }

            var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
            try
            {
                Bot.SendReply(Methods.GetLocaleString(langs.FirstOrDefault(e => e.Name.Equals("English")).Doc, "GetLang"), update, keyboard:menu);
            }
            catch (AggregateException e)
            {
                ErrorReporter.Report(e, "Sending language menu");
                foreach (var ex in e.InnerExceptions)
                {
                    var x = ex as ApiRequestException;

                     Bot.Send(x.Message, update.Message.Chat.Id);
                }
            }
            catch (ApiRequestException ex)
            {
                ErrorReporter.Report(ex, "Sending language menu");
                 Bot.Send(ex.Message, update.Message.Chat.Id);
            }
        }

        [Command(Trigger = "halt", DevOnly = true, Description = "Stop the bot")]
        public static void StopBot(Update update, string[] args)
        {
             Bot.SendReply("Stopping bot", update);
            if (update.Message.From.Id != Constants.Devs[0])
            {
                 Bot.Send($"The bot has been stopped by {update.Message.From.Id} {update.Message.From.FirstName}",
                    Constants.Devs[0]);
            }
            try
            {
                Redis.SaveRedis();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Saving Redis before halt");
            }
            Environment.Exit(0);
        }

        [Command(Trigger = "makelangadmin", RequiresReply = true, UploadAdmin = true)]
       public static void MakeLangAdmin(Update update, string[] args)
        {
            var id = update.Message.ReplyToMessage.From.Id;
            var res =  Redis.db.SetAddAsync("langAdmins", id).Result;
             Bot.SendReply("Done", update);
        }

        [Command(Trigger = "removelangadmin", RequiresReply = true, UploadAdmin = true)]
        public static void RemoveLangAdmin(Update update, string[] args)
        {
            var id = update.Message.ReplyToMessage.From.Id;
            var res =  Redis.db.SetRemoveAsync("langAdmins", id).Result;
             Bot.SendReply("Done", update);
        }
        [Command(Trigger = "reloadLang", UploadAdmin = true)]
        public static void reloadLang(Update update, string[] args)
        {
            Methods.IntialiseLanguages();
            Bot.SendReply("Done", update);
        }

        [Command(Trigger = "remotedisablewatch", GlobalAdminOnly = true, Description = "Exempt a user in a group: user:group")]
        public static void RemoteDisbleMediaExcempt(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            long userId = 0;
            long groupd = 0;
            if (args.Length == 2)
            {
                long temp;
                var spilt = args[1].Split(':');
                if (long.TryParse(spilt[0], out temp))
                {
                    userId = temp;
                    groupd = long.Parse(spilt[1]);
                }
            }
            Redis.db.SetAddAsync($"chat:{groupd}:watch", userId);
            Bot.Send(Methods.GetLocaleString(lang, "off"), update.Message.From.Id);
            //Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "getrekt", GlobalAdminOnly = true, Description = "Global ban: id:reason, or reply")]
        public static void GlobalBan(Update update, string[] args)
        {
            long userId = 0;
            string moti = "";
            if (update.Message.ReplyToMessage != null)
            {
                if (update.Message.ReplyToMessage.ForwardFrom != null)
                {
                    userId = update.Message.ReplyToMessage.ForwardFrom.Id;
                }
                else
                {
                    userId = update.Message.ReplyToMessage.From.Id;
                }
                moti = args[1];
            }            
            if (args.Length == 2)
            {
                long temp;
                var spilt = args[1].Split(':');
                if (long.TryParse(spilt[0], out temp))
                {
                    userId = temp;
                    moti = spilt[1];
                }
                else
                {
                    moti = args[1];
                }                
            }
            else
            {
                return;
            }
            if (userId != 0)
            {
                 if (TryRekt(userId, moti, update))
                     Bot.SendReply($"{userId} has been rekt for {moti}", update);
            }
            else
            {
                 Bot.SendReply("Nopes", update);
            }
        }

        [Command(Trigger = "getrektu", GlobalAdminOnly = true, Description = "Global ban by @username: name:reason")]
        public static void GlobalBanListUsername(Update update, string[] args)
        {
            string moti = "";
            string idlist = "";
            if (args.Length == 2)
            {
                int temp;
                var spilt = args[1].Split(':');
                moti = spilt[1];
                var usernames = spilt[0].Split(',');
                foreach (var username in usernames)
                {
                    try
                    {
                        var userId = Methods.ResolveIdFromusername(username);
                        if (TryRekt(userId, moti, update))
                            idlist = $"{idlist}, {userId}";
                    }
                    catch (Exception e)
                    {
                        // An unknown username is a typo, not a bug; the reply says so.
                        if (e.Message != "UnableToResolveUsername")
                            ErrorReporter.Report(e, $"Global ban of {username}");
                        Bot.SendReply(e.Message, update);
                    }
                }
                Bot.SendReply($"{idlist} has been rekt for {moti}", update);
            }
            else
            {
                Bot.SendReply("Nopes", update);
            }
        }

        [Command(Trigger = "bangroup", DevOnly = true, Description = "Stop serving a group: group id")]
        public static void BanGroup(Update update, string[] args)
        {
            if (args.Length == 2)
            {
                var groupId = args[1];
                // A bad id throws; the dispatcher reports it.
                var id = long.Parse(groupId);
                Redis.db.SetAddAsync("bot:bannedGroups", id);
                Bot.SendReply("Group banned", update);
            }
        }

        [Command(Trigger = "unbangroup", DevOnly = true, Description = "Serve a banned group again: group id")]
        public static void UnbanGroup(Update update, string[] args)
        {
            if (args.Length == 2)
            {
                var groupId = args[1];
                // A bad id throws; the dispatcher reports it.
                var id = long.Parse(groupId);
                Redis.db.SetRemoveAsync("bot:bannedGroups", id);
                Bot.SendReply("Group unbanned", update);
            }
        }

        [Command(Trigger = "getrekti", GlobalAdminOnly = true, Description = "Global ban a list of ids: id,id:reason")]
        public static void GlobalBanListids(Update update, string[] args)
        {
            string moti = "";
            string idlist = "";
            if (args.Length == 2)
            {
                var spilt = args[1].Split(':');
                moti = spilt[1];
                var usernames = spilt[0].Split(',');
                foreach (var username in usernames)
                {
                    try
                    {
                        var userId = long.Parse(username);
                        if (TryRekt(userId, moti, update))
                            idlist = $"{idlist}, {userId}";
                    }
                    catch (Exception e)
                    {
                        // An id that is not a number is a typo, not a bug; the reply says so.
                        if (!(e is FormatException || e is OverflowException))
                            ErrorReporter.Report(e, $"Global ban of {username}");
                        Bot.SendReply(e.Message, update);
                    }
                }
                Bot.SendReply($"{idlist} has been rekt for {moti}", update);
            }
            else
            {
                Bot.SendReply("Nopes", update);
            }
        }

        [Command(Trigger = "allowp", GlobalAdminOnly = true, InGroupOnly = true, Description = "Allow this group to use the premium bot")]
        public static void AllowPremiumBot(Update update, string[] args)
        {
             Redis.db.SetAddAsync("premiumBot", update.Message.Chat.Id);
             Bot.SendReply("Activated", update);
        }

        [Command(Trigger = "blockp", GlobalAdminOnly = true, InGroupOnly = true, Description = "Stop this group using the premium bot")]
        public static void BlockPremiumBot(Update update, string[] args)
        {
             Redis.db.SetRemoveAsync("premiumBot", update.Message.Chat.Id);
             Bot.SendReply("Deactivated", update);
        }

        [Command(Trigger = "unrekt", GlobalAdminOnly = true, Description = "Lift a global ban: id:reason")]
        public static void Unrekt(Update update, string[] args)
        {
            long userId = 0;
            string moti = "";
            if (update.Message.ReplyToMessage != null)
            {
                if (update.Message.ReplyToMessage.ForwardFrom != null)
                {
                    userId = update.Message.ReplyToMessage.ForwardFrom.Id;
                }
                else
                {
                    userId = update.Message.ReplyToMessage.From.Id;
                }
                moti = args[1];
            }
            if (args.Length == 2)
            {
                long temp;
                var spilt = args[1].Split(':');
                if (long.TryParse(spilt[0], out temp))
                {
                    userId = temp;
                    moti = spilt[1];
                }
                else
                {
                    moti = args[1];
                }
            }
            else
            {
                return;
            }
            if (userId != 0)
            {
                 if (!GlobalBansReady(update)) return;
                 bool lifted;
                 try
                 {
                     lifted = GlobalBans.UnbanAsync(userId, moti).GetAwaiter().GetResult();
                 }
                 catch (Exception e)
                 {
                     ErrorReporter.Report(e, $"Lifting the global ban of {userId}");
                     Bot.SendReply($"Could not save lifting the global ban of {userId}: {e.Message}", update);
                     return;
                 }
                 Bot.SendReply(lifted
                     ? $"{userId} has been unrekt for {moti}"
                     : $"{userId} has no global ban", update);
            }
            else
            {
                 Bot.SendReply("Nopes", update);
            }
        }

        [Command(Trigger = "leave", DevOnly = true, Description = "Make the bot leave a chat: chat id")]
        public static void LeaveChat(Update update, string[] args)
        {
            try
            {
                var chatId = args[1];
                 Bot.Api.LeaveChat(chatId);
                 Bot.SendReply("The chat has been left", update);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Leaving chat");
            }
        }

        /// <summary>How many active global bans /rektlist lists, newest first; a message holds 4096 characters.</summary>
        internal const int RektListShown = 30;

        [Command(Trigger = "rektlist", GlobalAdminOnly = true, Description = "List the active global bans")]
        public static void GlobalBanList(Update update, string[] args)
        {
            if (!GlobalBansReady(update)) return;
            IReadOnlyList<Data.GlobalBanRecord> bans;
            try
            {
                bans = Data.Repositories.GlobalBans.GetActiveAsync().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Listing global bans");
                Bot.SendReply($"Could not read the global bans: {e.Message}", update);
                return;
            }
            Bot.SendReply(FormatRektList(bans), update);
        }

        /// <summary>The /rektlist reply: the count, then the newest bans (HTML).</summary>
        internal static string FormatRektList(IReadOnlyList<Data.GlobalBanRecord> bans)
        {
            if (bans.Count == 0) return "No active global bans.";
            var lines = bans.Take(RektListShown).Select(b =>
                $"{b.UserId}: {System.Net.WebUtility.HtmlEncode(Shorten(b.Reason, 80))} ({b.ImposedAt:yyyy-MM-dd})");
            var header = bans.Count > RektListShown
                ? $"{bans.Count} active global bans, newest {RektListShown}:"
                : $"{bans.Count} active global bans:";
            return header + "\n" + string.Join("\n", lines);
        }

        private static string Shorten(string text, int length)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(no reason)";
            text = text.Replace('\n', ' ').Trim();
            return text.Length <= length ? text : text.Substring(0, length - 1) + "…";
        }

        /// <summary>
        /// Global bans live only in PostgreSQL. Says so, and returns false, while it is not ready
        /// (not configured, or the schema not up to date yet).
        /// </summary>
        private static bool GlobalBansReady(Update update)
        {
            if (Storage.PostgresStorage.Ready) return true;
            Bot.SendReply("Global bans are stored in PostgreSQL, which is not ready. Nothing was changed.", update);
            return false;
        }

        /// <summary>Bans one account everywhere. False, with a reply saying why, if it was not saved.</summary>
        private static bool TryRekt(long userId, string reason, Update update)
        {
            if (!GlobalBansReady(update)) return false;
            try
            {
                GlobalBans.BanAsync(userId, reason, update.Message.From.Id).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Global ban of {userId}");
                Bot.SendReply($"Could not save the global ban of {userId}: {e.Message}", update);
                return false;
            }
        }

        [Command(Trigger = "mediaid", DevOnly = true, RequiresReply = true, Description = "File id of the replied media")]
        public static void GetMediaId(Update update, string[] args)
        {
            var mediaID = Methods.GetMediaId(update.Message);
           
             Bot.SendReply($"Media: {mediaID}", update);
        }

        [Command(Trigger = "msgid", DevOnly = true, RequiresReply = true, Description = "Id of the replied message")]
        public static void GetMessageID(Update update, string[] args)
        {
           
            Bot.SendReply($"Message: {update.Message.ReplyToMessage.MessageId}", update);
        }

        [Command(Trigger = "langcode", Menu = Attributes.CommandMenu.Devs, Description = "Language code of a user")]
        public static void GetUserLangCode(Update update, string[] args)
        {
            try
            {
                if (update.Message.ReplyToMessage != null)
                {
                    if (update.Message.ReplyToMessage.ForwardFrom != null)
                    {
                        Bot.Send($"{update.Message.ReplyToMessage.ForwardFrom.LanguageCode}", update.Message.Chat.Id);
                    }
                    else
                    {
                        Bot.Send($"{update.Message.ReplyToMessage.From.LanguageCode}", update.Message.Chat.Id);
                    }
                }
                else
                {
                    Bot.Send($"{update.Message.From.LanguageCode}", update.Message.Chat.Id);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Getting language code");
                Bot.SendReply(e.Message, update);
            }
        }


        [Command(Trigger = "getuser", GroupAdminOnly = true, Menu = Attributes.CommandMenu.Devs, Description = "A user's record with global ban and message count")]
        public static void GetUserDetails(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
            var userid = Methods.GetUserId(update, args);
            var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
            text = $"{text}{FormatGlobalBan(userid)}";
            var msgs = Redis.db.HashGetAsync($"chat:{userid}", "msgs").Result;
            text = $"{text}\nUser has said {msgs} ever";
            if (update.Message.Chat.Type != ChatType.Private)
            {
                var status = Bot.Api.GetChatMember(update.Message.Chat.Id, userid).Result;
                text = $"{text}\n The user is a {status.Status.ToString()} in this chat";
            }
             Bot.SendReply(text, update);
        }

        /// <summary>
        /// The /getuser lines about the member's most recent global ban (HTML), each starting with a
        /// newline; empty when there never was one. Global bans live only in PostgreSQL.
        /// </summary>
        internal static string FormatGlobalBan(long userId)
        {
            if (!Storage.PostgresStorage.Ready) return "\nGlobal ban: unknown (PostgreSQL is not ready)";
            Data.GlobalBanRecord ban;
            try
            {
                ban = Data.Repositories.GlobalBans.GetLatestAsync(userId).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Reading the global ban of {userId}");
                return "\nGlobal ban: unknown (could not read it)";
            }
            return FormatGlobalBan(ban);
        }

        internal static string FormatGlobalBan(Data.GlobalBanRecord ban)
        {
            if (ban == null) return "";
            var text = ban.Status == "active"
                ? $"\nGlobal ban: active since {ban.ImposedAt:yyyy-MM-dd HH:mm} UTC"
                : $"\nGlobal ban: lifted {ban.ResolvedAt:yyyy-MM-dd HH:mm} UTC";
            text += $"\nGlobal ban reason: {System.Net.WebUtility.HtmlEncode(ban.Reason ?? "(none)")}";
            if (ban.Status != "active")
                text += $"\nLifted because: {System.Net.WebUtility.HtmlEncode(ban.ResolutionReason ?? "(none)")}";
            return text;
        }

        [Command(Trigger = "whois", Menu = Attributes.CommandMenu.Devs, Description = "Name, id and username of a user")]
        public static void WhoIs(Update update, string[] args)
        {
            var id = Methods.GetUserId(update, args);
            var name = Methods.GetName(id);
            var username = Methods.GetUsername(id);           
            Bot.SendReply($"Name: {name}\nID: {id}\nUsername: {username}", update);

        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "validate")]
        public static void validateLang(CallbackQuery query, string[] args)
        {
            if (!Methods.IsLangAdmin(query.From.Id) && query.Message.Chat.Id != Constants.TranslatorsId) return;

            var command = args[0];
            var choice = "";
            if (args.Length > 2)
                choice = args[2];
            //choice = args[1];
            if (choice == "All")
            {
                LanguageHelper.ValidateFiles(query.Message.Chat.Id, query.Message.MessageId);
            }

            if (args[4] != "base" && args[3] == "All")
            {
                LanguageHelper.ValidateFiles(query.Message.Chat.Id, query.Message.MessageId, choice);
            }

            var vlang = Program.LangaugeList.Where(e => e.Name.Equals(choice)).FirstOrDefault();

            //var menu = new ReplyKeyboardHide { HideKeyboard = true, Selective = true };
            //Bot.SendTextMessage(id, "", replyParameters: new ReplyParameters { MessageId = update.Message.MessageId }, replyMarkup: menu);
            LanguageHelper.ValidateLanguageFile(query.Message.Chat.Id, vlang.FilePath, query.Message.MessageId);
        }

        [Callback(Trigger = "getlang")]
        public static void getLang(CallbackQuery query, string[] args)
        {
            if (!Methods.IsLangAdmin(query.From.Id) && query.Message.Chat.Id != Constants.TranslatorsId) return;

            var command = args[0];
            var choice = "";
            if (args.Length > 2)
                choice = args[2];
            if (choice == "All")
            {
                Bot.ReplyToCallback(query, "One moment...");
                LanguageHelper.SendAllFiles(query.Message.Chat.Id);
            }

            if (args[4] != "base" && args[3] == "All")
            {
                Bot.ReplyToCallback(query, "One moment...");
                LanguageHelper.SendBase(choice, query.Message.Chat.Id);
            }

            var glang = Program.LangaugeList.FirstOrDefault(e => e.Name.Equals(choice));
            Bot.ReplyToCallback(query, "One moment...");
            LanguageHelper.SendFile(query.Message.Chat.Id, glang.Name);
        }

        /// <summary>The Review menu of a language upload. Who may tap is checked inside, so others get an answer.</summary>
        [Callback(Trigger = LanguageUpload.BatchMenu.Prefix)]
        public static void LanguageUploadMenu(CallbackQuery query, string[] args) =>
            LanguageUpload.LanguageUploads.HandleAsync(query, args).GetAwaiter().GetResult();

        [Callback(Trigger = "upload", UploadAdmin = true)]
        public static void uploadLang(CallbackQuery query, string[] args)
        {
            // New/Old buttons from before the Review menu: their pending files are no longer kept.
            Bot.ReplyToCallback(query, "This upload is from an older version. Upload the file again.", edit: false, showAlert: true);
        }
    }
}
