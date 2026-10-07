using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Helpers;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Models
{
    /// <summary>
    /// Everything the per-message handlers need from Redis, fetched once per update as a single
    /// pipelined batch.
    ///
    /// Each handler used to run on its own thread-pool task and issue its own blocking reads, so
    /// one group text message cost ~20 sequential round-trips - the same chat:{id}:watch set alone
    /// was queried five separate times. Handlers now read from here instead.
    /// </summary>
    internal sealed class MessageContext
    {
        public Update Update;
        public Message Message;
        public long ChatId;
        public long UserId;
        public bool IsGroup;

        /// <summary>
        /// When the poll loop received this update. Staleness checks use it instead of "now" so
        /// that time spent in the update queue does not count against the message - otherwise a
        /// backlog silently switches flood and length enforcement off.
        /// </summary>
        public DateTime ReceivedAt;

        /// <summary>User is on the chat's watch list, and so exempt from every automated action.</summary>
        public bool Watched;

        /// <summary>
        /// False when any read failed. Moderation must not run on a partial context: an absent
        /// value reads as "not configured", which for the watch list means "not exempt" and for
        /// chat:{id}:settings means "flood checking enabled". Acting on that would ban people the
        /// chat had explicitly excluded. Previously a failed read threw and the handler was skipped;
        /// this preserves that, while still letting commands and stats run.
        /// </summary>
        public bool Complete;

        public XDocument Lang;

        public HashEntry[] Settings;        // chat:{id}:settings
        public HashEntry[] Flood;           // chat:{id}:flood
        public HashEntry[] FloodExceptions; // chat:{id}:floodexceptions
        public HashEntry[] Media;           // chat:{id}:media
        public HashEntry[] Char;            // chat:{id}:char
        public HashEntry[] NameLength;      // chat:{id}:antinamelengthsettings
        public HashEntry[] TextLength;      // chat:{id}:antitextlengthsettings
        public HashEntry[] GlobalBan;       // globalBan:{whoever IsRekt should check}
        public RedisValue SpamCount;        // spam:{chat}:{user}

        /// <summary>
        /// The chat's inline bot blocklist. Only read when the message was sent via an inline bot,
        /// so ordinary messages pay nothing for it; empty otherwise.
        /// </summary>
        public IReadOnlyList<InlineBotBlock> InlineBotBlocks = Array.Empty<InlineBotBlock>();

        /// <summary>
        /// The message is a sticker from a pack on the chat's sticker pack blocklist. Only looked up
        /// for stickers that belong to a pack; false otherwise.
        /// </summary>
        public bool StickerSetBlocked;

        /// <summary>
        /// The chat's handling of the channel this message was posted as. Only read for posts made as
        /// a channel (not anonymous admins); null otherwise.
        /// </summary>
        public ChannelPostPolicy ChannelPosts;

        /// <summary>Reads one field out of a fetched hash. Returns RedisValue.Null when absent.</summary>
        public static RedisValue Field(HashEntry[] entries, string name)
        {
            if (entries == null) return RedisValue.Null;
            foreach (var entry in entries)
            {
                if (entry.Name == name) return entry.Value;
            }
            return RedisValue.Null;
        }

        public RedisValue Setting(string name) => Field(Settings, name);
        public RedisValue FloodSetting(string name) => Field(Flood, name);
        public RedisValue MediaSetting(string name) => Field(Media, name);
        public RedisValue CharSetting(string name) => Field(Char, name);

        /// <summary>
        /// Issues every read the hot path needs at once. SE.Redis pipelines them onto the single
        /// connection, so the batch costs roughly one round-trip of latency rather than one each,
        /// and the awaiting consumer holds no thread while it is in flight.
        /// </summary>
        internal static async Task<MessageContext> BuildAsync(Update update, DateTime receivedAt)
        {
            var message = update.Message;
            var context = new MessageContext
            {
                Update = update,
                Message = message,
                ReceivedAt = receivedAt,
                ChatId = message.Chat.Id,
                UserId = message.From.Id,
                IsGroup = message.Chat.Type != ChatType.Private
            };

            if (!context.IsGroup)
            {
                // Private chats run none of the group handlers; only the language is ever needed.
                try
                {
                    context.Lang = (await Methods.GetGroupLanguageAsync(context.ChatId))?.Doc;
                }
                catch (Exception e)
                {
                    LogHelper.Error($"Language lookup for {context.ChatId} failed: {e.Message}");
                }
                context.Complete = true; // no moderation runs in private chats
                return context;
            }

            // IsRekt checks the joining user on a join message, otherwise the sender.
            var globalBanId = message.NewChatMember?.Id ?? context.UserId;

            var db = Redis.db;
            var watch = db.SetContainsAsync($"chat:{context.ChatId}:watch", context.UserId);
            var settings = db.HashGetAllAsync($"chat:{context.ChatId}:settings");
            var flood = db.HashGetAllAsync($"chat:{context.ChatId}:flood");
            var floodExceptions = db.HashGetAllAsync($"chat:{context.ChatId}:floodexceptions");
            var media = db.HashGetAllAsync($"chat:{context.ChatId}:media");
            var characters = db.HashGetAllAsync($"chat:{context.ChatId}:char");
            var nameLength = db.HashGetAllAsync($"chat:{context.ChatId}:antinamelengthsettings");
            var textLength = db.HashGetAllAsync($"chat:{context.ChatId}:antitextlengthsettings");
            var globalBan = db.HashGetAllAsync($"globalBan:{globalBanId}");
            var spamCount = db.StringGetAsync($"spam:{context.ChatId}:{context.UserId}");
            var language = Methods.GetGroupLanguageAsync(context.ChatId);
            var inlineBotBlocks = message.ViaBot != null
                ? Repositories.InlineBotBlocks.GetAsync(context.ChatId)
                : Task.FromResult<IReadOnlyList<InlineBotBlock>>(Array.Empty<InlineBotBlock>());
            var stickerSet = StickerSetName.Normalise(message.Sticker?.SetName);
            var stickerSetBlocked = stickerSet != null
                ? Repositories.StickerSetBlocks.ContainsAsync(context.ChatId, stickerSet)
                : Task.FromResult(false);
            var channelPosts = message.SenderChat != null && message.SenderChat.Id != context.ChatId
                ? Repositories.ChannelPosts.GetPolicyAsync(context.ChatId, message.SenderChat.Id)
                : Task.FromResult<ChannelPostPolicy>(null);

            // Deliberately tolerant of individual failures. One slow settings hash must not discard
            // the whole update - that would silently drop commands under exactly the Redis
            // conditions this batching exists to survive. A field that could not be read is left
            // absent, which every consumer already treats as "not configured".
            try
            {
                await Task.WhenAll(watch, settings, flood, floodExceptions, media, characters,
                    nameLength, textLength, globalBan, spamCount, language, inlineBotBlocks, channelPosts,
                    stickerSetBlocked);
            }
            catch (Exception)
            {
                // Reported once below, with the consequence spelled out.
            }

            context.Settings = Value(settings);
            context.Flood = Value(flood);
            context.FloodExceptions = Value(floodExceptions);
            context.Media = Value(media);
            context.Char = Value(characters);
            context.NameLength = Value(nameLength);
            context.TextLength = Value(textLength);
            context.GlobalBan = Value(globalBan);
            context.SpamCount = Value(spamCount);
            context.Lang = Value(language)?.Doc;
            context.InlineBotBlocks = Value(inlineBotBlocks) ?? Array.Empty<InlineBotBlock>();
            context.ChannelPosts = Value(channelPosts);
            context.StickerSetBlocked = Value(stickerSetBlocked);

            context.Complete = Ok(watch) && Ok(settings) && Ok(flood) && Ok(floodExceptions) &&
                               Ok(media) && Ok(characters) && Ok(nameLength) && Ok(textLength) &&
                               Ok(globalBan) && Ok(spamCount) && Ok(language) && Ok(inlineBotBlocks) &&
                               Ok(channelPosts) && Ok(stickerSetBlocked);

            // Fail closed: if we could not read the watch list, treat the user as exempt rather
            // than as fair game.
            context.Watched = Ok(watch) ? watch.Result : true;

            if (!context.Complete)
                LogHelper.Error($"Partial context for {context.ChatId}; skipping moderation for this message.");

            return context;
        }

        private static bool Ok(Task task) => task.Status == TaskStatus.RanToCompletion;

        /// <summary>Result of a completed read, or default if it faulted or was cancelled.</summary>
        private static T Value<T>(Task<T> task)
        {
            return Ok(task) ? task.Result : default(T);
        }
    }
}
