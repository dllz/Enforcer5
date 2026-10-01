using System;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5
{
    /// <summary>
    /// Admin commands for the per-chat inline bot blocklist. Enforcement lives in
    /// OnMessage.BlockedInlineBot and storage behind <see cref="IInlineBotBlockRepository"/>.
    /// </summary>
    public static partial class Commands
    {
        /// <summary>
        /// Every entry is tested against each message sent via an inline bot, so this bounds the
        /// per-message cost. With <see cref="InlineBotPattern.MaxLength"/> it also keeps the
        /// /blockedinline listing inside a single Telegram message.
        /// </summary>
        private const int MaxInlineBotBlocks = 50;

        [Command(Trigger = "blockinline", InGroupOnly = true, GroupAdminOnly = true)]
        public static void BlockInlineBot(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!TryReadInlineBotBlock(update, args, lang, out var block)) return;

            var chatId = update.Message.Chat.Id;
            var repository = Repositories.InlineBotBlocks;
            if (repository.CountAsync(chatId).GetAwaiter().GetResult() >= MaxInlineBotBlocks)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "inlineBlockLimit", MaxInlineBotBlocks), update);
                return;
            }

            var added = repository.AddAsync(chatId, block).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, added ? "inlineBlockAdded" : "inlineBlockExists", block), update);
            if (added) Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "unblockinline", InGroupOnly = true, GroupAdminOnly = true)]
        public static void UnblockInlineBot(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!TryReadInlineBotBlock(update, args, lang, out var block)) return;

            var removed = Repositories.InlineBotBlocks.RemoveAsync(update.Message.Chat.Id, block).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, removed ? "inlineBlockRemoved" : "inlineBlockNotFound", block), update);
            if (removed) Service.LogCommand(update, update.Message.Text);
        }

        // Admin-only: a public list of the patterns would tell spammers how to get around them.
        [Command(Trigger = "blockedinline", InGroupOnly = true, GroupAdminOnly = true)]
        public static void BlockedInlineBots(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var blocks = Repositories.InlineBotBlocks.GetAsync(update.Message.Chat.Id).GetAwaiter().GetResult();
            if (blocks.Count == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "inlineBlockListEmpty"), update);
                return;
            }

            var list = string.Join("\n", blocks
                .OrderBy(block => block.Kind)
                .ThenBy(block => block.Value, StringComparer.OrdinalIgnoreCase)
                .Select(block => block.ToString()));
            Bot.SendReply(Methods.GetLocaleString(lang, "inlineBlockList", list), update);
        }

        /// <summary>
        /// Reads the entry from the command argument or, failing that, from the bot behind the
        /// replied-to message. Replies with the reason and returns false if there is no valid entry.
        /// </summary>
        private static bool TryReadInlineBotBlock(Update update, string[] args, XDocument lang, out InlineBotBlock block)
        {
            var input = args.Length > 1 ? args[1] : null;
            if (string.IsNullOrWhiteSpace(input))
            {
                var viaBot = update.Message.ReplyToMessage?.ViaBot;
                input = viaBot?.Username != null ? $"@{viaBot.Username}" : null;
            }

            string reply;
            switch (InlineBotBlock.TryParse(input, out block, out var detail))
            {
                case InlineBotBlockParseResult.Ok:
                    return true;
                case InlineBotBlockParseResult.InvalidUsername:
                    reply = Methods.GetLocaleString(lang, "inlineBlockInvalidUsername", input.Trim());
                    break;
                case InlineBotBlockParseResult.PatternTooLong:
                    reply = Methods.GetLocaleString(lang, "inlineBlockPatternTooLong", InlineBotPattern.MaxLength);
                    break;
                case InlineBotBlockParseResult.InvalidPattern:
                    reply = Methods.GetLocaleString(lang, "inlineBlockInvalidPattern", detail);
                    break;
                default:
                    reply = Methods.GetLocaleString(lang, "inlineBlockUsage");
                    break;
            }
            Bot.SendReply(reply, update);
            return false;
        }
    }
}
