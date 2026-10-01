using System;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    /// <summary>
    /// Admin commands for posts made as a channel: the per-chat allowlist, and banning or unbanning
    /// a channel through /ban and /unban. The filter itself is OnMessage.ChannelPost; storage is
    /// behind <see cref="IChannelPostRepository"/>.
    /// </summary>
    public static partial class Commands
    {
        /// <summary>Bounds the allowlist, and keeps /allowedchannels inside one message.</summary>
        private const int MaxAllowedChannels = 50;

        [Command(Trigger = "allowchannel", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AllowChannel(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!TryResolveChannel(update, args, lang, out var channel)) return;

            var chatId = update.Message.Chat.Id;
            var repository = Repositories.ChannelPosts;
            if (repository.CountAllowedAsync(chatId).GetAwaiter().GetResult() >= MaxAllowedChannels)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "channelAllowLimit", MaxAllowedChannels), update);
                return;
            }

            var added = repository.AllowAsync(chatId, channel).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, added ? "channelAllowed" : "channelAlreadyAllowed", channel), update);
            if (added) Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "disallowchannel", InGroupOnly = true, GroupAdminOnly = true)]
        public static void DisallowChannel(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!TryResolveChannel(update, args, lang, out var channel)) return;

            var removed = Repositories.ChannelPosts.DisallowAsync(update.Message.Chat.Id, channel.Id).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, removed ? "channelDisallowed" : "channelNotAllowed", channel), update);
            if (removed) Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "allowedchannels", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AllowedChannels(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var channels = Repositories.ChannelPosts.GetAllowedAsync(update.Message.Chat.Id).GetAwaiter().GetResult();
            if (channels.Count == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "allowedChannelsEmpty"), update);
                return;
            }

            var list = string.Join("\n", channels
                .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.ToString()));
            Bot.SendReply(Methods.GetLocaleString(lang, "allowedChannelsList", list), update);
        }

        /// <summary>Bans a channel from posting in the chat. Used by /ban.</summary>
        private static void BanChannel(Update update, long channelId, string title, XDocument lang)
        {
            var channel = new AllowedChannel(channelId, title ?? LookUpTitle(channelId));
            try
            {
                Bot.Api.BanChatSenderChat(update.Message.Chat.Id, channelId).Wait();
            }
            catch (Exception e)
            {
                Methods.SendError(Bot.AsApiError(e)?.Message ?? e.Message, update.Message, lang);
                return;
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "channelBanned", channel,
                Methods.GetNick(update.Message, null, true)), update);
            Service.LogCommand(update, update.Message.Text);
        }

        /// <summary>Lets a banned channel post in the chat again. Used by /unban.</summary>
        private static void UnbanChannel(Update update, long channelId, string title, XDocument lang)
        {
            var channel = new AllowedChannel(channelId, title ?? LookUpTitle(channelId));
            try
            {
                Bot.Api.UnbanChatSenderChat(update.Message.Chat.Id, channelId).Wait();
            }
            catch (Exception e)
            {
                Methods.SendError(Bot.AsApiError(e)?.Message ?? e.Message, update.Message, lang);
                return;
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "channelUnbanned", channel), update);
            Service.LogCommand(update, update.Message.Text);
        }

        /// <summary>
        /// Works out which channel an allowlist command means: the replied-to post (posted as, or
        /// forwarded from, a channel), a channel id, or a public @channel. Replies with the reason
        /// and returns false when there is none.
        /// </summary>
        private static bool TryResolveChannel(Update update, string[] args, XDocument lang, out AllowedChannel channel)
        {
            channel = null;
            var chatId = update.Message.Chat.Id;
            var argument = ChannelPosts.FirstWord(args.Length > 1 ? args[1] : null);

            Chat found = null;
            if (argument == null)
            {
                found = ChannelPosts.FromReply(update.Message.ReplyToMessage);
            }
            else if (ChannelPosts.TryParseChannelId(argument, out var id))
            {
                found = new Chat { Id = id, Type = ChatType.Channel, Title = LookUpTitle(id) };
            }
            else if (ChannelPosts.IsUsername(argument))
            {
                try
                {
                    var info = Bot.Api.GetChat(argument).GetAwaiter().GetResult();
                    if (info.Type == ChatType.Channel)
                        found = new Chat { Id = info.Id, Type = info.Type, Title = info.Title, Username = info.Username };
                }
                catch (Exception)
                {
                    // Not a chat the bot can see; reported below.
                }
                if (found == null)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "channelNotFound", argument), update);
                    return false;
                }
            }

            if (found == null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "channelUsage"), update);
                return false;
            }
            if (found.Id == chatId)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "channelIsGroup"), update);
                return false;
            }

            channel = new AllowedChannel(found.Id, found.Title);
            return true;
        }

        /// <summary>The channel's title, or null if the bot cannot see the channel.</summary>
        private static string LookUpTitle(long channelId)
        {
            try
            {
                return Bot.Api.GetChat(channelId).GetAwaiter().GetResult().Title;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
