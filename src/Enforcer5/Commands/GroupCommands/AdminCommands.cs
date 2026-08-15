using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Xml.Linq;
using Telegram.Bot;
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using Enforcer5.Storage.PrincipalContent;
using Enforcer5.Storage.Moderation;
#pragma warning disable CS4014
namespace Enforcer5
{
    public static partial class Commands
    {

        [Command(Trigger = "setrules", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetRules(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.SendReply(input, update);
                    Methods.WriteGroupContent(update.Message.Chat.Id, GroupContentTypes.Rules, input);
                    var res = Bot.Api.EditMessageText(update.Message.Chat.Id, result.MessageId,
                         Methods.GetLocaleString(lang, "RulesSet"));
                    Service.LogCommand(update, update.Message.Text);
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "addrule", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AddRule(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var rules = Methods.ReadGroupContent(update.Message.Chat.Id, GroupContentTypes.Rules);
                    var result = Bot.SendReply(input, update);
                    Methods.WriteGroupContent(update.Message.Chat.Id, GroupContentTypes.Rules, $"{rules}\n{input}");
                    var res = Bot.Api.EditMessageText(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "RulesSet"));
                    Service.LogCommand(update, update.Message.Text);
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "setabout", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetAbout(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.SendReply(input, update);
                    Methods.WriteGroupContent(update.Message.Chat.Id, GroupContentTypes.About, input);
                    var res = Bot.Api.EditMessageText(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "AboutSet"));
                    Service.LogCommand(update, update.Message.Text);
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "user", InGroupOnly = true, GroupAdminOnly = true)]
        public static void User(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                if (userid == Bot.Me.Id)
                    return;
                var userMenu = new Menu(2);
                userMenu.Buttons = new List<InlineButton>
                {
                    new InlineButton(Methods.GetLocaleString(lang, "removeWarn"), $"userbuttonremwarns:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "resetWarn"), $"userbuttonresetwarn:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "ban"), $"userbuttonbanuser:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "Warn"), $"userbuttonwarnuser:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "removePreWarn"), $"removePrewarn:{update.Message.Chat.Id}:{userid}"),
                    new InlineButton(Methods.GetLocaleString(lang, "resetPreWarn"), $"resetPrewarns:{update.Message.Chat.Id}:{userid}"),
                };

                var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
                Bot.SendReply(text, update, Key.CreateMarkupFromMenu(userMenu));
            }
            catch (Exception e)
            {
                if (e.Message.Equals("UnableToResolveId"))
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "UnableToGetID"), update);
                }
                else
                {
                    Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang);
                }
            }


        }

        [Command(Trigger = "extra", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Extra(Update update, string[] args)
        {
            if (args[1] == null)
                return;
            if (!args[1].StartsWith("#"))
                return;

            int splitindex = 0;
            if (args[1].Contains(" ") && args[1].Contains("\n"))
            {
                splitindex = args[1].IndexOf(" ") < args[1].IndexOf("\n")
                    ? args[1].IndexOf(" ")
                    : args[1].IndexOf("\n");
            }
            else if (args[1].Contains(" "))
            {
                splitindex = args[1].IndexOf(" ");
            }
            else if (args[1].Contains("\n"))
            {
                splitindex = args[1].IndexOf("\n");
            }
            else if (update.Message.ReplyToMessage == null) return;

            var words = splitindex != 0
                ? new[] { args[1].Substring(1, splitindex).Trim(), args[1].Substring(splitindex + 1) }
                : new[] { args[1].Substring(1).Trim(), null };
            words[0] = $"#{words[0]}";
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (update.Message.ReplyToMessage != null && words[1] == null)
            {
                var fileId = Methods.GetMediaId(update.Message.ReplyToMessage);
                if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.Text))
                {
                    string text = update.Message.ReplyToMessage.Text;
                    try
                    {
                        var res = Bot.SendReply(text, update);
                        var result = res;
                        Methods.WriteCustomCommand(new CustomCommand(update.Message.Chat.Id, words[0], text,
                            null, null, null));
                        var resulted = Bot.Api.EditMessageText(update.Message.Chat.Id, result.MessageId,
                            Methods.GetLocaleString(lang, "extraMediaSaved", words[0]));
                        Service.LogCommand(update, update.Message.Text);
                    }
                    catch (AggregateException e)
                    {

                        if (e.Message.Contains("message is too long"))
                        {

                            Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                        }
                        else if (e.Message.Contains("Unsupported start tag"))
                        {
                            Bot.SendReply(
                                Methods.GetLocaleString(lang, "markdownBroken"), update);
                        }
                        else
                        {
                            Methods.SendError($"{e.Message}", update.Message, lang);
                        }
                    }
                }
                else
                {
                    var type = Methods.GetMediaType(update.Message.ReplyToMessage);
                    if (!string.IsNullOrEmpty(type))
                    {
                        Methods.WriteCustomCommand(new CustomCommand(update.Message.Chat.Id, words[0], null,
                            type, fileId, null, OperationsStore.CurrentEdition));
                        Bot.Send(Methods.GetLocaleString(lang, "extraMediaSaved", words[0]), update);
                        Service.LogCommand(update, update.Message.Text);
                        return;
                    }
                    return;
                }
                return;
            }
            else if (update.Message.ReplyToMessage != null && words[1] != null)
            {
                var type = Methods.GetMediaType(update.Message.ReplyToMessage);
                if (!string.IsNullOrEmpty(type))
                {
                    if (type.Equals("gif"))
                    {
                        var toSave = update.Message.ReplyToMessage.Document.FileId;
                        string text = words[1];
                        try
                        {
                            var res = Bot.SendReply(text, update);
                            var result = res;
                            Methods.WriteCustomCommand(new CustomCommand(update.Message.Chat.Id, words[0], text,
                                "gif", toSave, null, OperationsStore.CurrentEdition));
                            var resulted = Bot.Api.EditMessageText(update.Message.Chat.Id, result.MessageId,
                                Methods.GetLocaleString(lang, "extraMediaSaved", words[0]));
                            Service.LogCommand(update, update.Message.Text);
                        }
                        catch (AggregateException e)
                        {

                            if (e.Message.Contains("message is too long"))
                            {

                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                            }
                            else if (e.Message.Contains("Unsupported start tag"))
                            {
                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "markdownBroken"), update);
                            }
                            else
                            {
                                Methods.SendError($"{e.Message}", update.Message, lang);
                            }
                        }
                    }
                    else
                    {
                        string text = words[1];
                        try
                        {
                            var fileId = Methods.GetMediaId(update.Message.ReplyToMessage);
                            Methods.WriteCustomCommand(new CustomCommand(update.Message.Chat.Id, words[0], null,
                                type, fileId, words[1], OperationsStore.CurrentEdition));
                            Bot.Send(Methods.GetLocaleString(lang, "extraMediaSaved", words[0]), update);
                            Service.LogCommand(update, update.Message.Text);
                        }
                        catch (AggregateException e)
                        {

                            if (e.Message.Contains("message is too long"))
                            {

                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                                    update);
                            }
                            else if (e.Message.Contains("Unsupported start tag"))
                            {
                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "markdownBroken"), update);
                            }
                            else
                            {
                                Methods.SendError($"{e.Message}", update.Message, lang);
                            }
                        }
                    }
                }
            }
            else
            {
                string text = words[1];
                try
                {
                    var res = Bot.SendReply(text, update);
                    var result = res;
                    Methods.WriteCustomCommand(new CustomCommand(update.Message.Chat.Id, words[0], text,
                        null, null, null));
                    var resulted = Bot.Api.EditMessageText(update.Message.Chat.Id, result.MessageId,
                        Methods.GetLocaleString(lang, "extraSaved", words[0]));
                    Service.LogCommand(update, update.Message.Text);
                }
                catch (AggregateException e)
                {

                    if (e.Message.Contains("message is too long"))
                    {

                        Bot.SendReply(
                            Methods.GetLocaleString(lang, "tooLong", Methods.GetLocaleString(lang, "extra")),
                            update);
                    }
                    else if (e.Message.Contains("Unsupported start tag"))
                    {
                        Bot.SendReply(
                            Methods.GetLocaleString(lang, "markdownBroken"), update);
                    }
                    else
                    {
                        Methods.SendError($"{e.Message}", update.Message, lang);
                    }
                }

            }
        }

        [Command(Trigger = "extralist", InGroupOnly = true)]
        public static void ExtraList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var commands = Methods.ListCustomCommands(update.Message.Chat.Id);

            if (commands.Count == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "noExtra"), update);
            }
            else
            {
                var text = string.Join("\n", commands.Select(command => command.Trigger));

                if (Methods.SendInPm(update.Message, "Extra"))
                {
                    lang = Methods.GetGroupLanguage(update.Message, false).Doc;
                    Bot.SendToPm(Methods.GetLocaleString(lang, "extraList", text), update);
                    Bot.SendReply(Methods.GetLocaleString(lang, "botPm", text), update);
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "extraList", text), update);
                }
            }
        }

        [Command(Trigger = "extradel", InGroupOnly = true, GroupAdminOnly = true)]
        public static void ExtraDelete(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (args[1] != null)
            {
                if (Methods.DeleteCustomCommand(update.Message.Chat.Id, args[1]))
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "extraDeleted"), update);
                    Service.LogCommand(update, update.Message.Text);
                }
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "noExtra"), update);
            }
        }

        [Callback(Trigger = "xcr", GroupAdminOnly = true)]
        public static void ResolveExtraConflict(CallbackQuery call, string[] args)
        {
            if (args.Length < 3 || !long.TryParse(args[1], out var chatId) ||
                !Guid.TryParseExact(args[2], "N", out var candidateId)) return;
            var database = PostgresDatabase.FromConfiguration();
            CustomCommand command;
            try
            {
                command = new CustomCommandConflictRepository(database).ResolveAsync(chatId, candidateId)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Bot.ReplyToCallback(call, command == null
                ? "This custom-command conflict was already resolved."
                : $"Kept #{command.Trigger} and deleted the other duplicates.", replyMarkup: command == null
                    ? null
                    : new InlineKeyboardMarkup(new[] { new InlineKeyboardButton("Undo",
                        $"xcu:{chatId}:{candidateId:N}") }));
        }

        [Callback(Trigger = "xcd", GroupAdminOnly = true)]
        public static void DeleteExtraConflict(CallbackQuery call, string[] args)
        {
            if (args.Length < 3 || !long.TryParse(args[1], out var chatId) ||
                !Guid.TryParseExact(args[2], "N", out var candidateId)) return;
            var database = PostgresDatabase.FromConfiguration();
            bool deleted;
            try
            {
                deleted = new CustomCommandConflictRepository(database).DeleteAllAsync(chatId, candidateId)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Bot.ReplyToCallback(call, deleted
                ? "Deleted all duplicate custom commands."
                : "This custom-command conflict was already resolved.", replyMarkup: deleted
                    ? new InlineKeyboardMarkup(new[] { new InlineKeyboardButton("Undo",
                        $"xcu:{chatId}:{candidateId:N}") })
                    : null);
        }

        [Callback(Trigger = "xcu", GroupAdminOnly = true)]
        public static void UndoExtraConflict(CallbackQuery call, string[] args)
        {
            if (args.Length < 3 || !long.TryParse(args[1], out var chatId) ||
                !Guid.TryParseExact(args[2], "N", out var candidateId)) return;
            var database = PostgresDatabase.FromConfiguration();
            bool restored;
            try
            {
                restored = new CustomCommandConflictRepository(database).UndoAsync(chatId, candidateId)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Bot.ReplyToCallback(call, restored
                ? "Restored the duplicate custom commands. Run the trigger again to choose."
                : "This custom-command change can no longer be undone.");
        }

        [Command(Trigger = "setlink", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetLink(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var link = "";
            if (update.Message.Chat.Username != null)
            {
                link = $"https://t.me/{update.Message.Chat.Username}";
                Methods.WriteGroupContent(update.Message.Chat.Id, GroupContentTypes.Link, link);
                Bot.SendReply(Methods.GetLocaleString(lang, "linkSet"), update);
                Service.LogCommand(update, update.Message.Text);
            }
            else
            {
                if (args.Length == 2)
                {
                    if (args[1].ToLower().Equals("no"))
                    {
                        Methods.DeleteGroupContent(update.Message.Chat.Id, GroupContentTypes.Link);
                        Bot.SendReply(Methods.GetLocaleString(lang, "linkSet"), update);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else
                    {
                        link = args[1];
                        Methods.WriteGroupContent(update.Message.Chat.Id, GroupContentTypes.Link, link);
                        Bot.SendReply(Methods.GetLocaleString(lang, "linkSet"), update);
                        Service.LogCommand(update, update.Message.Text);
                    }
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "NoLinkInputed"), update);
                }
            }
        }

        [Command(Trigger = "status", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Status(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            long userId = 0;
            var chatId = update.Message.Chat.Id;
            if (args.Length == 2)
            {
                if (long.TryParse(args[1], out userId))
                {

                }
                else
                {
                    if (!string.IsNullOrEmpty(args[1]))
                    {
                        if (args[1].StartsWith("@"))
                            userId = Methods.ResolveIdFromusername(args[1], chatId);
                    }
                }
                if (userId > 0)
                {
                    var user = Bot.Api.GetChatMember(chatId, userId).Result;
                    var status = user.Status;
                    var name = user.User.FirstName;
                    if (user.User.Username != null)
                        name = $"{name} - @{user.User.Username}";
                    if (update.Message.Chat.Type == ChatType.Group && Methods.IsBanned(chatId, userId))
                        status = ChatMemberStatus.Kicked;
                    var reason = StorageRouting.GroupPostgresIsPrimary(chatId)
                        ? (RedisValue)(OperationsStore.GetActiveSanctionAsync(chatId, userId,
                            SanctionKinds.Ban).Result?.Reason ?? string.Empty)
                        : Redis.db.HashGetAsync($"chat:{chatId}:bannedlist:{userId}", "why").Result;
                    if (!reason.IsNullOrEmpty && status == ChatMemberStatus.Kicked)
                    {

                        Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}", name, Methods.GetLocaleString(lang, "bannedFor", reason)), update);
                    }
                    else
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}", name, ""), update);
                    }
                }
            }
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
                var user = Bot.Api.GetChatMember(chatId, userId).Result;
                var status = user.Status;
                var name = user.User.FirstName;
                if (user.User.Username != null)
                    name = $"{name} - @{user.User.Username}";
                if (update.Message.Chat.Type == ChatType.Group && Methods.IsBanned(chatId, userId))
                    status = ChatMemberStatus.Kicked;
                var reason = StorageRouting.GroupPostgresIsPrimary(chatId)
                    ? (RedisValue)(OperationsStore.GetActiveSanctionAsync(chatId, userId,
                        SanctionKinds.Ban).Result?.Reason ?? string.Empty)
                    : Redis.db.HashGetAsync($"chat:{chatId}:bannedlist:{userId}", "why").Result;
                if (!reason.IsNullOrEmpty && status == ChatMemberStatus.Kicked)
                {

                    Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}", name, Methods.GetLocaleString(lang, "bannedFor", reason)), update);
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, $"status{status.ToString()}", name, ""), update);
                }
            }

        }

        [Command(Trigger = "welcome", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetWelcome(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var chatId = update.Message.Chat.Id;
            var settings = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Settings, "DeleteLastWelcome").Result;
            if (!settings.HasValue)
            {
                tempAddDeleteLastWelcomeSetting(update, chatId);
            }

            if (args.Length == 2)
            {
                if (!string.IsNullOrEmpty(args[1]))
                {
                    GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Welcome, "hasmedia", "false").GetAwaiter().GetResult();
                    if (update.Message.ReplyToMessage != null)
                    {
                        var repliedTo = Methods.GetMediaType(update.Message.ReplyToMessage);
                        if (repliedTo.Equals("gif"))
                        {
                            var fileId = Methods.GetMediaId(update.Message.ReplyToMessage);
                            GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Welcome, "hasmedia", "true").GetAwaiter().GetResult();
                            GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Welcome, "media", fileId).GetAwaiter().GetResult();
                            Service.LogCommand(update, update.Message.Text);
                        }
                    }
                    if (args[1].Equals("a"))
                    {
                        SetWelcomeFields(chatId, "composed", "a");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("r"))
                    {
                        SetWelcomeFields(chatId, "composed", "r");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("m"))
                    {
                        SetWelcomeFields(chatId, "composed", "m");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("ar") || args[1].Equals("ra"))
                    {
                        SetWelcomeFields(chatId, "composed", "ra");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("mr") || args[1].Equals("rm"))
                    {
                        SetWelcomeFields(chatId, "composed", "rm");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("am") || args[1].Equals("ma"))
                    {
                        SetWelcomeFields(chatId, "composed", "am");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("ram") || args[1].Equals("rma") || args[1].Equals("arm") || args[1].Equals("amr") || args[1].Equals("mra") || args[1].Equals("mar"))
                    {
                        SetWelcomeFields(chatId, "composed", "ram");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else if (args[1].Equals("no"))
                    {
                        SetWelcomeFields(chatId, "composed", "no");
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update.Message);
                        Service.LogCommand(update, update.Message.Text);
                    }
                    else
                    {
                        SetWelcomeFields(chatId, "custom", args[1]);
                        try
                        {
                            var res = Bot.SendReply(args[1], update);
                            var result = Bot.Api.EditMessageText(chatId, res.MessageId, Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName));
                            Service.LogCommand(update, update.Message.Text);
                        }
                        catch (AggregateException e)
                        {
                            SetWelcomeFields(chatId, "composed", "no");
                            Bot.SendReply(e.Message, update);
                        }
                    }
                }
                else if (update.Message.ReplyToMessage != null)
                {
                    var repliedTo = Methods.GetMediaType(update.Message.ReplyToMessage);
                    if (repliedTo.Equals("gif"))
                    {
                        var fileID = Methods.GetMediaId(update.Message.ReplyToMessage);
                        SetWelcomeFields(chatId, "media", args[1]);
                        GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Welcome, "media", fileID).GetAwaiter().GetResult();
                        GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Welcome, "hasmedia", true).GetAwaiter().GetResult();
                        Bot.SendReply(Methods.GetLocaleString(lang, "welcomeSet", update.Message.From.FirstName), update);
                        Service.LogCommand(update, update.Message.Text);
                    }
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "incorrectArgument"), update);
                }
            }
        }

        public static void tempAddDeleteLastWelcomeSetting(Update update, long chatId)
        {
            GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Settings, "DeleteLastWelcome", "no").GetAwaiter().GetResult();
            var text = "We have set DeleteWelcomeLastMessage to 'no'. You can change this in the group settings menu.";
            Bot.SendReply(text, update.Message);
        }

        private static void SetWelcomeFields(long chatId, string type, string content)
        {
            GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Welcome, "type", type).GetAwaiter().GetResult();
            GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Welcome, "content", content).GetAwaiter().GetResult();
        }

        [Command(Trigger = "disablewatch", InGroupOnly = true, GroupAdminOnly = true)]
        public static void DisbleMediaExcempt(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var userId = update.Message.From.Id;
            var chatId = update.Message.Chat.Id;
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            PolicyStore.SetWatchDisabledAsync(chatId, userId, true).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, "off"), update);
            Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "enablewatch", InGroupOnly = true, GroupAdminOnly = true)]
        public static void EnableMediaExcempt(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var userId = update.Message.From.Id;
            var chatId = update.Message.Chat.Id;
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            PolicyStore.SetWatchDisabledAsync(chatId, userId, false).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, "on"), update);
            Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "check", InGroupOnly = true, GroupAdminOnly = true)]
        public static void CheckGroupUser(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                try
                {
                    var userid = Methods.GetUserId(update, args);
                    var status = Check(userid, update.Message.Chat.Id);
                    var n = OperationsStore.GetGroupUserMessagesAsync(update.Message.Chat.Id, userid).Result;
                    var number = n;
                    Bot.SendReply(Methods.GetLocaleString(lang, $"usergroupstatus{status}", userid, number), update.Message);
                }
                catch (Exception e)
                {
                    Methods.SendError(e.Message, update.Message, lang);
                }
            }
            catch (AggregateException e)
            {
                Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang);
            }
        }

        public static string Check(long userid, long chatid)
        {
            var status = Bot.Api.GetChatMember(chatid, userid).Result.Status;
            var priv = RoleStore.IsAuthorizedAsync(chatid, userid).Result;
            var elevated = RoleStore.IsModeratorAsync(chatid, userid).Result;

            if (status == ChatMemberStatus.Creator) return "creator";
            if (priv) return "auth";
            if (status == ChatMemberStatus.Administrator) return "admin";
            if (status == ChatMemberStatus.Kicked) return "banned";
            if (elevated) return "elevated";
            if (status == ChatMemberStatus.Member) return "member";
            if (status == ChatMemberStatus.Left) return "left";
            return "neverseen";
        }

        [Command(Trigger = "addlogchannel", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AddLogChat(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var role = Bot.Api.GetChatMember(chat, Convert.ToInt32((long)update.Message.From.Id));
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator || priv)
            {
                long channelId;

                var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
                if (long.TryParse(args[1], out channelId))
                {
                    if (channelId == chat)
                        return;
                    GroupSettingsStore.SetLogChatAsync(update.Message.Chat.Id, channelId).GetAwaiter().GetResult();
                    Service.LogCommand(update, update.Message.Text);

                    Bot.SendReply(Methods.GetLocaleString(lang, "channelAdded"), update);
                }
                else if (update.Message.ReplyToMessage != null)
                {
                    if (update.Message.ReplyToMessage.ForwardFromChat != null)
                    {
                        channelId = update.Message.ReplyToMessage.ForwardFromChat.Id;
                        if (channelId == chat)
                            return;
                        GroupSettingsStore.SetLogChatAsync(update.Message.Chat.Id, channelId).GetAwaiter().GetResult();
                        Service.LogCommand(update, update.Message.Text);

                        Bot.SendReply(Methods.GetLocaleString(lang, "channelAdded"), update);
                    }
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "notChannel"), update);
                }
            }
        }

        [Command(Trigger = "dellogchannel", InGroupOnly = true, GroupAdminOnly = true)]
        public static void DeleteLogChannel(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var role = Bot.Api.GetChatMember(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator || priv)
            {
                var lang = Methods.GetGroupLanguage(chat).Doc;
                Service.LogCommand(update, update.Message.Text);
                GroupSettingsStore.SetLogChatAsync(update.Message.Chat.Id, null).GetAwaiter().GetResult();
                Bot.Send(Methods.GetLocaleString(lang, "logchannelRemoved"), update);
            }
        }

        [Command(Trigger = "temptime", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetDefaultTempban(Update update, string[] args)
        {
            int time;
            var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
            if (!int.TryParse(args[1], out time))
            {

                time = Methods.GetGroupTempbanTime(update.Message.Chat.Id);
                Bot.SendReply(Methods.GetLocaleString(lang, "defaultTime", time), update);
            }
            else
            {
                if (!Methods.IsGroupAdmin(update) &
                    !Methods.IsGlobalAdmin(update.Message.From.Id) & !Constants.Devs.Contains(update.Message.From.Id))
                {
                    Bot.SendReply(
                        Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message, true).Doc,
                            "userNotAdmin"), update.Message);
                    return;
                }
                GroupSettingsStore.SetAsync(update.Message.Chat.Id, GroupSettingGroups.Other, "tempbanTime", time).GetAwaiter().GetResult();
                Bot.SendReply(Methods.GetLocaleString(lang, "defaultTimeSet"), update);
                Service.LogCommand(update, update.Message.Text);
            }
        }

        [Command(Trigger = "settempmutetime", InGroupOnly = true, GroupAdminOnly = true)]
        public static void SetDefaultTempMute(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
            long userId = 0;
            var chatId = update.Message.Chat.Id;
            long time;
            string length = "";
            string units = "";
            
            if (args[1] != null)
            {
                if (args[1].Contains(' '))
                {
                    length = args[1].Split(' ')[0];
                    
                    try
                    {
                        units = args[1].Split(' ')[1];
                    }
                    catch (Exception e)
                    {
                        units = "min";
                    }
                }
                else 
                {
                    length = args[1];
                    units = "min";
                }
                if (long.TryParse(length, out time))
                {
                    time = long.Parse(length);
                    if (time == 0)
                    {
                        time = Methods.GetGroupTempMuteTime(update.Message.Chat.Id);
                    }
                }
                else
                {
                    time = Methods.GetGroupTempMuteTime(update.Message.Chat.Id);
                }
                double calculatedTime = 0;
                switch (units)
                {
                    case "min":
                    case "mins":
                    case "minutes":
                    case "minute":
                        calculatedTime = TimeSpan.FromMinutes(time).TotalMinutes;
                        break;
                    case "hour":
                    case "hours":
                        calculatedTime = TimeSpan.FromHours(time).TotalMinutes;
                        break;
                    case "days":
                    case "day":
                        calculatedTime = TimeSpan.FromDays(time).TotalMinutes;
                        break;
                    default:
                        calculatedTime = TimeSpan.FromMinutes(time).TotalMinutes;
                        break;

                }
                GroupSettingsStore.SetAsync(update.Message.Chat.Id, GroupSettingGroups.Other, "tempMuteTime", calculatedTime).GetAwaiter().GetResult();
                Bot.SendReply(Methods.GetLocaleString(lang, "defaultMuteTimeSet"), update);
                Service.LogCommand(update, update.Message.Text);
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "incorrectArgument"), update);
            }
        }

        [Command(Trigger = "media", InGroupOnly = true, GroupAdminOnly = true)]
        public static void MediaUserMenu(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
            var warns = WarningStore.GetBalanceAsync(chatId, userid, WarningTypes.Media)
                .GetAwaiter().GetResult().ActiveQuantity;
            var text = Methods.GetLocaleString(lang, "getMediaWarn", warns);
            var userMenu = new Menu(2);
            userMenu.Buttons = new List<InlineButton>
            {
                new InlineButton(Methods.GetLocaleString(lang, "removeWarn"), $"usermediaremwarns:{update.Message.Chat.Id}:{userid}"),
                new InlineButton(Methods.GetLocaleString(lang, "resetWarn"), $"usermediaresetwarn:{update.Message.Chat.Id}:{userid}")

            };
            Bot.SendReply(text, update, Key.CreateMarkupFromMenu(userMenu));
        }

        [Command(Trigger = "listmutedjoiners", InGroupOnly = true, GroupAdminOnly = true)]
        public static void GetMutedJoinersList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var mutedJoinerIds = StorageRouting.GroupPostgresIsPrimary(update.Message.Chat.Id)
                ? OperationsStore.ListActiveSanctionsForGroupAsync(update.Message.Chat.Id, SanctionKinds.Mute)
                    .Result.Where(value => value.LegacyKey?.StartsWith("new-joiner:") == true)
                    .Select(value => (RedisValue)value.UserId).ToArray()
                : Redis.db.SetMembers($"chat:{update.Message.Chat.Id}:mutedJoiners");

            if (mutedJoinerIds.Length == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "noMutedJoiners"), update);
            }
            else
            {
                List<string> userNames = new List<string>();
                foreach (var id in mutedJoinerIds)
                {
                    var string_id = long.Parse(id.ToString())
;                    var user = Bot.Api.GetChatMember(update.Message.Chat.Id, string_id).Result;
                    var username = user.User.Username;
                    userNames.Add(username);
                }

                
                var text = string.Join("\n", userNames);

                if (Methods.SendInPm(update.Message, "Muted"))
                {
                    lang = Methods.GetGroupLanguage(update.Message, false).Doc;
                    Bot.SendToPm(Methods.GetLocaleString(lang, "mutedList", text), update);
                    Bot.SendReply(Methods.GetLocaleString(lang, "botPm", text), update);
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "mutedList", text), update);
                }
            }
        }

        [Command(Trigger = "unmutenewjoiners", InGroupOnly = true, GroupAdminOnly = true)]
        public static void UnmuteNewJoiners(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var chatId = update.Message.Chat.Id;
            var joiners = StorageRouting.GroupPostgresIsPrimary(chatId)
                ? OperationsStore.ListActiveSanctionsForGroupAsync(chatId, SanctionKinds.Mute)
                    .Result.Where(value => value.LegacyKey?.StartsWith("new-joiner:") == true)
                    .Select(value => (RedisValue)value.UserId).ToArray()
                : Redis.db.SetMembers($"chat:{chatId}:mutedJoiners");
            List<long> failedToUnmute = new List<long>();

            if (joiners.Length == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "noMutedJoiners"), update);
            }
            else
            {
                foreach(var joiner in joiners)
                {
                    var js = joiner.ToString();
                    long joiner_id = long.Parse(joiner.ToString());
                    var res = Methods.UnmuteUser(update.Message.Chat.Id, joiner_id, lang);
                    if (!res)
                    {
                        failedToUnmute.Add(joiner_id);
                    }
                }
                if (failedToUnmute.Count > 0)
                {
                    var text = string.Join("\n", failedToUnmute);

                    if (Methods.SendInPm(update.Message, "Muted"))
                    {
                        lang = Methods.GetGroupLanguage(update.Message, false).Doc;
                        Bot.SendToPm(Methods.GetLocaleString(lang, "failedUnmutedList", text), update);
                        Bot.SendReply(Methods.GetLocaleString(lang, "botPm", text), update);
                    }
                    else
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, "failedUnmutedList", text), update);
                    }
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "unmutedAllJoiners"), update);
                }

            }
        }


        public static void tempAddMuteOnJoinSetting(Update update, long chatId)
        {
            GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Settings, "MuteOnJoin", "yes").GetAwaiter().GetResult();
            var text = "We have set Mute on join setting to 'no'. You can change this in the group settings menu.";
            Bot.SendReply(text, update.Message);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "userbuttonresetwarn", GroupAdminOnly = true)]
        public static void UserButtonRemWarns(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = long.Parse(args[2]);
            WarningStore.ResetAsync(call.Message.Chat.Id, userId, WarningTypes.Standard)
                .GetAwaiter().GetResult();
             Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnsReset", call.From.FirstName));
        }

        [Callback(Trigger = "userbuttonremwarns", GroupAdminOnly = true)]
        public static void removeWarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = long.Parse(args[2]);
            WarningStore.DecrementAsync(call.Message.Chat.Id, userId, WarningTypes.Standard)
                .GetAwaiter().GetResult();
            Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnRemoved", call.From.FirstName));
        }

        [Callback(Trigger = "userbuttonbanuser", GroupAdminOnly = true)]
        public static void UserButtonsBanUser(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = args[2];
            var isChatMember = Bot.Api.GetChatMember(call.Message.Chat.Id,long.Parse(userId)).Result;
            var res = Methods.BanUser(call.Message.Chat.Id, long.Parse(userId), lang);
            var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{call.Message.Chat.Id}:tempbanned", userId).Result;
            if (isAlreadyTempbanned)
            {
                var all = Redis.db.HashGetAllAsync("tempbanned").Result;
                foreach (var mem in all)
                {
                    if ($"{call.Message.Chat.Id}:{userId}".Equals(mem.Value))
                    {
                         Redis.db.HashDeleteAsync("tempbanned", mem.Name);
                    }
                }
                 Redis.db.SetRemoveAsync($"chat:{call.Message.Chat.Id}:tempbanned", userId);
            }
            if (res)
            {
                if (isChatMember.Status == ChatMemberStatus.Member)
                {
                    Methods.SaveBan(long.Parse(userId), "ban");
                }
                if (long.Parse(userId) == 321720895 | long.Parse(userId) == 9375804)
                {
                    Redis.db.SetAdd("bot:lookaround",
                        $"{long.Parse(userId)}:\n{call.Message.Chat.Id} {call.Message.Chat.Title} {call.Message.From.Id} {call.Message.From.FirstName}");
                }
                Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
                    Methods.GetLocaleString(lang, "userBanned"));
            }
             
        }

        [Callback(Trigger = "userbuttonwarnuser", GroupAdminOnly = true)]
        public static void UserButtonsWarnUser(CallbackQuery call, string[] args)
        {
            var userId = long.Parse(args[2]);
            var chatId = long.Parse(args[1]);
            if (Methods.IsGroupAdmin(userId, chatId))
            {
                return;
                
            }
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var num = WarningStore.IssueAsync(chatId, userId, WarningTypes.Standard)
                .GetAwaiter().GetResult().Balance;
            var max = 3;
            int.TryParse(GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Warn, "max").Result.ToString(), out max);
            
            if (num >= max)
            {
                var configuredType = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Warn, "type").Result;
                var type = configuredType.HasValue
                    ? configuredType.ToString()
                    : "kick";
                if (type.Equals("ban"))
                {
                    try
                    {
                         var res = Methods.BanUser(call.Message.Chat.Id, userId, lang);
                         var result = Bot.Api.EditMessageText(chatId, call.Message.MessageId, Methods.GetLocaleString(lang, "warnMaxBan", userId), parseMode: ParseMode.Html);
                    }
                    catch (AggregateException e)
                    {
                        Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", call.Message, lang);
                    }
                }
                else
                {
                    Methods.KickUser(call.Message.Chat.Id, userId, lang);
                    Bot.Api.EditMessageText(chatId, call.Message.MessageId, Methods.GetLocaleString(lang, "warnMaxKick", userId), parseMode: ParseMode.Html);
                }
                WarningStore.ResetAsync(chatId, userId, WarningTypes.Standard)
                    .GetAwaiter().GetResult();
            }
            else
            {
                var text = Methods.GetLocaleString(lang, "warn", userId, num, max);
                var baseMenu = new List<InlineKeyboardButton>();
                baseMenu.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang, "resetWarn"),
                    $"resetwarns:{chatId}:{userId}"));
                baseMenu.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang, "removeWarn"),
                    $"removewarn:{chatId}:{userId}"));
                var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
                 Bot.Api.EditMessageText(chatId, call.Message.MessageId, text, replyMarkup:menu, parseMode:ParseMode.Html);
            }
        }

        [Callback(Trigger = "usermediaremwarns", GroupAdminOnly = true)]
        public static void removeMediaWarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = long.Parse(args[2]);
            WarningStore.DecrementAsync(call.Message.Chat.Id, userId, WarningTypes.Media)
                .GetAwaiter().GetResult();
            Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnRemoved", call.From.FirstName));
        }

        [Callback(Trigger = "usermediaresetwarn", GroupAdminOnly = true)]
        public static void RESETmediawarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = long.Parse(args[2]);
            WarningStore.ResetAsync(call.Message.Chat.Id, userId, WarningTypes.Media)
                .GetAwaiter().GetResult();
            Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnRemoved", call.From.FirstName));
        }
    }
}
