using System;
using System.Collections.Generic;
using System.Linq;
using Enforcer5.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5.Models
{
    /// <summary>What /block and /unblock act on (Daniel, 2026-11-09): one command for every blocklist.</summary>
    internal enum BlockKind
    {
        /// <summary>One file (<c>banned_media</c>): whoever posts it again has it deleted.</summary>
        File,
        StickerSet,
        EmojiSet,
        InlineBot,
        /// <summary>A bare pack name, before Telegram said whether it is stickers or custom emoji.</summary>
        Pack
    }

    /// <param name="Value">The file's unique id, the pack's name (lowercase), or the inline bot's entry.</param>
    /// <param name="MediaType">A file's content type (<see cref="MessageFingerprint.ContentTypeOf"/>).</param>
    internal sealed record BlockTarget(BlockKind Kind, string Value, string MediaType = null, InlineBotBlock Bot = null);

    internal enum BlockParseResult { Ok, Empty, Invalid, InvalidUsername, InvalidPattern, PatternTooLong }

    /// <summary>Reading /block's and /unblock's argument and the message they reply to. Pure, for tests.</summary>
    internal static class BlockTargets
    {
        /// <summary>
        /// An argument: <c>@bot</c> or <c>/regex/</c> (inline bots), an <c>addstickers</c> or <c>addemoji</c> link, or a
        /// bare pack name (<see cref="BlockKind.Pack"/>: which kind of pack takes a lookup).
        /// </summary>
        internal static BlockParseResult Parse(string input, out BlockTarget target, out string detail)
        {
            target = null;
            detail = null;
            var text = input?.Trim();
            if (string.IsNullOrEmpty(text)) return BlockParseResult.Empty;
            if (text.StartsWith("@") || text.Length >= 2 && text[0] == '/' && text[^1] == '/')
            {
                switch (InlineBotBlock.TryParse(text, out var bot, out detail))
                {
                    case InlineBotBlockParseResult.Ok:
                        target = new BlockTarget(BlockKind.InlineBot, bot.ToString(), Bot: bot);
                        return BlockParseResult.Ok;
                    case InlineBotBlockParseResult.InvalidPattern:
                        return BlockParseResult.InvalidPattern;
                    case InlineBotBlockParseResult.PatternTooLong:
                        return BlockParseResult.PatternTooLong;
                    default:
                        return BlockParseResult.InvalidUsername;
                }
            }
            var name = StickerSetName.Parse(text);
            if (name == null) return BlockParseResult.Invalid;
            var lower = text.ToLowerInvariant();
            var kind = lower.Contains("addemoji") ? BlockKind.EmojiSet : lower.Contains("addstickers") ? BlockKind.StickerSet : BlockKind.Pack;
            target = new BlockTarget(kind, name);
            return BlockParseResult.Ok;
        }

        /// <summary>
        /// What a message offers to block, in this order: its sticker's pack, the packs of its custom emoji
        /// (<paramref name="emojiSets"/>, looked up by the caller), the inline bot it was sent via, and its file (a sticker's
        /// file is the sticker alone).
        /// </summary>
        internal static IReadOnlyList<BlockTarget> Of(Message message, IEnumerable<string> emojiSets)
        {
            var targets = new List<BlockTarget>();
            if (message == null) return targets;
            if (StickerSetName.Normalise(message.Sticker?.SetName) is { } pack) targets.Add(new BlockTarget(BlockKind.StickerSet, pack));
            foreach (var set in (emojiSets ?? Array.Empty<string>()).Where(s => s != null).Distinct(StringComparer.Ordinal))
                targets.Add(new BlockTarget(BlockKind.EmojiSet, set));
            if (message.ViaBot?.Username is { } username &&
                InlineBotBlock.TryParse("@" + username, out var bot, out _) == InlineBotBlockParseResult.Ok)
                targets.Add(new BlockTarget(BlockKind.InlineBot, bot.ToString(), Bot: bot));
            if (MessageFingerprint.MediaIdOf(message) is { } file)
                targets.Add(new BlockTarget(BlockKind.File, file, MessageFingerprint.ContentTypeOf(message)));
            return targets;
        }
    }
}
