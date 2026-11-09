using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    /// <summary>
    /// /block, /unblock and /blocked (Daniel, 2026-11-09): one command for every blocklist, replacing /blocksticker,
    /// /blockinline and /banmedia and their un- and list commands. What a reply offers (<see cref="BlockTargets.Of"/>): its
    /// sticker's pack, the packs of its custom emoji, the inline bot it was sent via, and its file. One thing is blocked at
    /// once; several are offered as buttons any admin may press (<see cref="CallBacks.BlockChoice"/>). An argument names
    /// one: <c>@bot</c> or <c>/regex/</c>, a pack link, or a pack's name (Telegram says whether it is stickers or emoji).
    /// The post replied to is deleted too, unless an admin made it. Enforcement: <c>OnMessage.BlockedInlineBot</c>,
    /// <c>OnMessage.BlockedStickerSet</c>, <see cref="EmojiCheck"/> and <see cref="BannedMedia"/>.
    /// </summary>
    public static partial class Commands
    {
        /// <summary>Each list stays inside one /blocked message; inline entries are tested against every inline post.</summary>
        private const int MaxStickerSetBlocks = 50;
        private const int MaxInlineBotBlocks = 50;

        internal const string BlockTrigger = "blk";
        private static readonly TimeSpan BlockChoiceFor = TimeSpan.FromMinutes(15);

        /// <summary>A /block reply waiting for an admin to choose what to block.</summary>
        private sealed record BlockChoice(long ChatId, Message Target, IReadOnlyList<BlockTarget> Targets, DateTime Until);

        private static readonly ConcurrentDictionary<string, BlockChoice> BlockChoices = new();

        [Command(Trigger = "block", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Block(Update update, string[] args)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            var input = args.Length > 1 ? args[1] : null;
            if (!string.IsNullOrWhiteSpace(input))
            {
                if (!TryReadBlockArgument(update, input, lang, out var target)) return;
                if (target.Kind is BlockKind.Pack or BlockKind.StickerSet or BlockKind.EmojiSet)
                {
                    // A typed name is checked against Telegram, since a typo would block nothing, and Telegram says
                    // which kind of pack it is.
                    var set = GetStickerSet(target.Value, out var notFound);
                    if (notFound)
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, "packNotFound", target.Value), update);
                        return;
                    }
                    if (set != null) target = target with { Kind = set.StickerType == StickerType.CustomEmoji ? BlockKind.EmojiSet : BlockKind.StickerSet };
                    else if (target.Kind == BlockKind.Pack)
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, "packLookupFailed", target.Value), update);
                        return;
                    }
                }
                var (line, added) = ApplyBlock(chatId, message.From.Id, target, null, lang);
                Bot.SendReply(line, update);
                if (added) Service.LogCommand(update, message.Text);
                return;
            }

            var replied = message.ReplyToMessage;
            if (replied == null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "blockUsage"), update);
                return;
            }
            var targets = BlockTargetsOf(replied);
            if (targets.Count == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "blockNothing"), update);
                return;
            }
            if (targets.Count == 1)
            {
                var (line, added) = ApplyBlock(chatId, message.From.Id, targets[0], replied, lang);
                if (added) DeleteBlockedPost(chatId, replied);
                Bot.SendReply(line, update);
                if (added) Service.LogCommand(update, message.Text);
                return;
            }

            foreach (var old in BlockChoices.Where(c => c.Value.Until < Clock.UtcNow).Select(c => c.Key).ToList()) BlockChoices.TryRemove(old, out _);
            var token = Guid.NewGuid().ToString("N")[..10];
            BlockChoices[token] = new BlockChoice(chatId, replied, targets, Clock.UtcNow + BlockChoiceFor);
            var rows = targets.Select((t, i) => new[]
                    { InlineKeyboardButton.WithCallbackData(BlockChoiceLabel(lang, t), $"{BlockTrigger}:{chatId}:{token}:{i}") })
                .Append(new[] { InlineKeyboardButton.WithCallbackData(Methods.GetLocaleString(lang, "blockChoiceAll"), $"{BlockTrigger}:{chatId}:{token}:all") });
            Bot.SendReply(Methods.GetLocaleString(lang, "blockChoose"), update, new InlineKeyboardMarkup(rows));
        }

        /// <summary>blk:{chat}:{token}:{index|all}, under /block's choice. Any admin of the group may choose.</summary>
        internal static void BlockChosen(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var token = args.Length > 2 ? args[2] : "";
            var choice = args.Length > 3 ? args[3] : "";
            if (!BlockChoices.TryRemove(token, out var pending) || pending.ChatId != chatId || pending.Until < Clock.UtcNow)
            {
                _ = Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "blockExpired"));
                if (call.Message != null) _ = Bot.Api.EditMessageReplyMarkup(call.Message.Chat.Id, call.Message.MessageId, null);
                return;
            }
            var chosen = choice == "all"
                ? pending.Targets
                : int.TryParse(choice, out var index) && index >= 0 && index < pending.Targets.Count
                    ? new[] { pending.Targets[index] }
                    : Array.Empty<BlockTarget>();
            var lines = new List<string>();
            var any = false;
            foreach (var target in chosen)
            {
                var (line, added) = ApplyBlock(chatId, call.From.Id, target, pending.Target, lang);
                lines.Add(line);
                any |= added;
            }
            if (any) DeleteBlockedPost(chatId, pending.Target);
            _ = Bot.Api.AnswerCallbackQuery(call.Id);
            if (call.Message != null && lines.Count > 0)
                Bot.Edit(call.Message.Chat.Id, call.Message.MessageId, string.Join("\n", lines)).Forget("Editing a /block choice");
        }

        [Command(Trigger = "unblock", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Unblock(Update update, string[] args)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            var input = (args.Length > 1 ? args[1] : null)?.Trim();
            var lines = new List<string>();
            if (string.Equals(input, "files", StringComparison.OrdinalIgnoreCase))
            {
                BannedMedia.CountAsync(chatId).GetAwaiter().GetResult();
                lines.Add(Methods.GetLocaleString(lang, "banMediaCleared", BannedMedia.Clear(chatId)));
            }
            else if (!string.IsNullOrEmpty(input))
            {
                if (!TryReadBlockArgument(update, input, lang, out var target)) return;
                var kinds = target.Kind == BlockKind.Pack ? new[] { BlockKind.StickerSet, BlockKind.EmojiSet } : new[] { target.Kind };
                foreach (var kind in kinds)
                    if (RemoveBlock(chatId, target with { Kind = kind }, lang) is { } line) lines.Add(line);
                if (lines.Count == 0) lines.Add(Methods.GetLocaleString(lang, "blockNotFound", target.Value));
            }
            else if (message.ReplyToMessage is { } replied)
            {
                lines.AddRange(BlockTargetsOf(replied).Select(t => RemoveBlock(chatId, t, lang)).Where(l => l != null));
                if (lines.Count == 0) lines.Add(Methods.GetLocaleString(lang, "unblockNothing"));
            }
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "blockUsage"), update);
                return;
            }
            Bot.SendReply(string.Join("\n", lines), update);
            Service.LogCommand(update, message.Text);
        }

        // Admin-only: a public list would tell spammers how to get around it.
        [Command(Trigger = "blocked", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Blocked(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var policy = DetectionSettings.GetAsync(chatId).GetAwaiter().GetResult();
            var files = BannedMedia.CountAsync(chatId).GetAwaiter().GetResult();
            Bot.SendReply(BlockedList(lang, policy, files), update);
        }

        /// <summary>The /blocked answer: each list that has entries, Detection's NSFW bans marked. Pure, for tests.</summary>
        internal static string BlockedList(XDocument lang, DetectionPolicy policy, int files)
        {
            var parts = new List<string>();
            if (policy.StickerSets.Count > 0)
                parts.Add(Methods.GetLocaleString(lang, "blockedStickerSets") + "\n" +
                          string.Join("\n", policy.StickerSets.OrderBy(n => n, StringComparer.Ordinal).Select(n => Ids.Mark(n))));
            var emoji = policy.EmojiSets.Where(e => e.Blocks).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            if (emoji.Count > 0)
                parts.Add(Methods.GetLocaleString(lang, "blockedEmojiSets") + "\n" + string.Join("\n", emoji.Select(e =>
                    Ids.Mark(e.Name) + (e.Kind == EmojiSetEntry.Auto ? " " + Methods.GetLocaleString(lang, "blockedAuto") : ""))));
            if (policy.InlineBots.Count > 0)
                parts.Add(Methods.GetLocaleString(lang, "blockedInlineBots") + "\n" + string.Join("\n", policy.InlineBots
                    .OrderBy(b => b.Kind).ThenBy(b => b.Value, StringComparer.OrdinalIgnoreCase).Select(b => b.ToString().FormatHTML())));
            if (files > 0) parts.Add(Methods.GetLocaleString(lang, "blockedFiles", files));
            return parts.Count == 0
                ? Methods.GetLocaleString(lang, "blockedNothing")
                : Methods.GetLocaleString(lang, "blockedTitle") + "\n\n" + string.Join("\n\n", parts);
        }

        /// <summary>What <paramref name="message"/> offers to block; its custom emoji are looked up for their packs.</summary>
        private static IReadOnlyList<BlockTarget> BlockTargetsOf(Message message)
        {
            var ids = CustomEmojis.IdsOf(message);
            var emoji = ids.Count == 0 ? null : CustomEmojis.ResolveAsync(ids).GetAwaiter().GetResult();
            var sets = ids.Where(id => emoji?.ContainsKey(id) == true).Select(id => emoji[id].SetName);
            return BlockTargets.Of(message, sets);
        }

        /// <summary>Blocks one thing. The line to answer with, and whether it is newly blocked.</summary>
        private static (string Line, bool Added) ApplyBlock(long chatId, long adminId, BlockTarget target, Message post, XDocument lang)
        {
            switch (target.Kind)
            {
                case BlockKind.File:
                {
                    var added = BannedMedia.Add(chatId, target.Value, target.MediaType, adminId, "command");
                    // Its image as a vector too, so edited copies of it are seen (similar_to_banned).
                    if (post != null) SimilarToBanned.FileBanned(chatId, post);
                    return (Methods.GetLocaleString(lang, added ? "banMediaAdded" : "banMediaAlready"), added);
                }
                case BlockKind.StickerSet:
                {
                    var repository = Repositories.StickerSetBlocks;
                    if (repository.ContainsAsync(chatId, target.Value).GetAwaiter().GetResult())
                        return (Methods.GetLocaleString(lang, "stickerBlockExists", Ids.Mark(target.Value)), false);
                    if (repository.CountAsync(chatId).GetAwaiter().GetResult() >= MaxStickerSetBlocks)
                        return (Methods.GetLocaleString(lang, "blockLimitStickerSets", MaxStickerSetBlocks), false);
                    var added = repository.AddAsync(chatId, target.Value).GetAwaiter().GetResult();
                    return (Methods.GetLocaleString(lang, added ? "stickerBlockAdded" : "stickerBlockExists", Ids.Mark(target.Value)), added);
                }
                case BlockKind.EmojiSet:
                {
                    var result = EmojiSetBlocks.BlockAsync(chatId, target.Value).GetAwaiter().GetResult();
                    return result switch
                    {
                        EmojiSetBlocks.Added.Full => (Methods.GetLocaleString(lang, "blockLimitEmojiSets", EmojiSetBlocks.MaxAdmin), false),
                        EmojiSetBlocks.Added.Already => (Methods.GetLocaleString(lang, "emojiBlockExists", Ids.Mark(target.Value)), false),
                        _ => (Methods.GetLocaleString(lang, "emojiBlockAdded", Ids.Mark(target.Value)), true)
                    };
                }
                case BlockKind.InlineBot:
                {
                    var repository = Repositories.InlineBotBlocks;
                    if (repository.GetAsync(chatId).GetAwaiter().GetResult().Contains(target.Bot))
                        return (Methods.GetLocaleString(lang, "inlineBlockExists", target.Bot), false);
                    if (repository.CountAsync(chatId).GetAwaiter().GetResult() >= MaxInlineBotBlocks)
                        return (Methods.GetLocaleString(lang, "blockLimitInlineBots", MaxInlineBotBlocks), false);
                    var added = repository.AddAsync(chatId, target.Bot).GetAwaiter().GetResult();
                    return (Methods.GetLocaleString(lang, added ? "inlineBlockAdded" : "inlineBlockExists", target.Bot), added);
                }
                default:
                    return (Methods.GetLocaleString(lang, "blockNothing"), false);
            }
        }

        /// <summary>Lifts one block. The line to answer with; null when it was not blocked.</summary>
        private static string RemoveBlock(long chatId, BlockTarget target, XDocument lang)
        {
            switch (target.Kind)
            {
                case BlockKind.File:
                    BannedMedia.CountAsync(chatId).GetAwaiter().GetResult();
                    return BannedMedia.Remove(chatId, target.Value) ? Methods.GetLocaleString(lang, "banMediaRemoved") : null;
                case BlockKind.StickerSet:
                    return Repositories.StickerSetBlocks.RemoveAsync(chatId, target.Value).GetAwaiter().GetResult()
                        ? Methods.GetLocaleString(lang, "stickerBlockRemoved", Ids.Mark(target.Value))
                        : null;
                case BlockKind.EmojiSet:
                    return EmojiSetBlocks.UnblockAsync(chatId, target.Value).GetAwaiter().GetResult()
                        ? Methods.GetLocaleString(lang, "emojiBlockRemoved", Ids.Mark(target.Value))
                        : null;
                case BlockKind.InlineBot:
                    return Repositories.InlineBotBlocks.RemoveAsync(chatId, target.Bot).GetAwaiter().GetResult()
                        ? Methods.GetLocaleString(lang, "inlineBlockRemoved", target.Bot)
                        : null;
                default:
                    return null;
            }
        }

        /// <summary>The post /block replied to goes too, unless an admin made it (as an anonymous admin, or themselves).</summary>
        private static void DeleteBlockedPost(long chatId, Message post)
        {
            if (post == null || post.SenderChat?.Id == chatId) return;
            if (post.SenderChat == null && (post.From == null || Methods.IsGroupAdmin(post.From.Id, chatId))) return;
            EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, post.MessageId)).GetAwaiter().GetResult();
        }

        private static string BlockChoiceLabel(XDocument lang, BlockTarget target) => target.Kind switch
        {
            BlockKind.StickerSet => Methods.GetLocaleString(lang, "blockChoiceStickerSet", target.Value),
            BlockKind.EmojiSet => Methods.GetLocaleString(lang, "blockChoiceEmojiSet", target.Value),
            BlockKind.InlineBot => Methods.GetLocaleString(lang, "blockChoiceInlineBot", target.Value),
            _ when target.MediaType == "sticker" => Methods.GetLocaleString(lang, "blockChoiceSticker"),
            _ => Methods.GetLocaleString(lang, "blockChoiceFile")
        };

        /// <summary>Reads /block's or /unblock's argument; replies with the reason and returns false when it is not one.</summary>
        private static bool TryReadBlockArgument(Update update, string input, XDocument lang, out BlockTarget target)
        {
            string reply;
            switch (BlockTargets.Parse(input, out target, out var detail))
            {
                case BlockParseResult.Ok:
                    return true;
                case BlockParseResult.InvalidUsername:
                    reply = Methods.GetLocaleString(lang, "inlineBlockInvalidUsername", input.Trim());
                    break;
                case BlockParseResult.PatternTooLong:
                    reply = Methods.GetLocaleString(lang, "inlineBlockPatternTooLong", InlineBotPattern.MaxLength);
                    break;
                case BlockParseResult.InvalidPattern:
                    reply = Methods.GetLocaleString(lang, "inlineBlockInvalidPattern", detail);
                    break;
                case BlockParseResult.Invalid:
                    reply = Methods.GetLocaleString(lang, "blockInvalid", input.Trim());
                    break;
                default:
                    reply = Methods.GetLocaleString(lang, "blockUsage");
                    break;
            }
            Bot.SendReply(reply, update);
            return false;
        }

        internal static void ResetBlockChoicesForTests() => BlockChoices.Clear();
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = Commands.BlockTrigger, GroupAdminOnly = true)]
        public static void BlockChoice(CallbackQuery call, string[] args) => Commands.BlockChosen(call, args);
    }
}
