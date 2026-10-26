using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Data
{
    /// <summary>
    /// What a batch of updates said about one Telegram account. <see cref="LastSeenAt"/> is set
    /// only when the account itself did something (sent a message, pressed a button); someone who
    /// is only replied to or added gives a profile but no activity.
    /// </summary>
    internal sealed record AccountObservation(long TelegramUserId, bool IsBot, string DisplayName, string Username,
        string LanguageCode, DateTimeOffset? LastSeenAt)
    {
        /// <summary><paramref name="newer"/> on top of this one.</summary>
        internal AccountObservation Then(AccountObservation newer) => newer with
        {
            // Telegram leaves language_code out of many updates; missing is not "none".
            LanguageCode = newer.LanguageCode ?? LanguageCode,
            LastSeenAt = Later(LastSeenAt, newer.LastSeenAt)
        };

        internal static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
            a == null ? b : b == null ? a : (a > b ? a : b);
    }

    /// <summary>
    /// What a batch of updates said about one chat. <see cref="Active"/> is false once the bot was
    /// seen leaving it. <see cref="ObservedUserCount"/> and <see cref="PublicLink"/> are filled in
    /// at flush time from Redis; null count, or <see cref="LinkKnown"/> false, leaves the stored value.
    /// </summary>
    internal sealed record ChatObservation(long TelegramChatId, string ChatType, string Title, string Username,
        DateTimeOffset? LastMessageAt, bool? Active, long? ObservedUserCount = null, bool LinkKnown = false,
        string PublicLink = null)
    {
        internal bool IsGroup => ChatType is "group" or "supergroup";

        internal ChatObservation Then(ChatObservation newer) => newer with
        {
            ChatType = newer.ChatType == "unknown" ? ChatType : newer.ChatType,
            Title = newer.Title ?? Title,
            // A chat seen only as a sender (a channel) or only via a ban carries no username; a
            // message from the group itself always does, null meaning it has none.
            Username = newer.Title != null ? newer.Username : Username,
            LastMessageAt = AccountObservation.Later(LastMessageAt, newer.LastMessageAt),
            Active = newer.Active ?? Active
        };

        internal static string TypeOf(ChatType type) => type switch
        {
            Telegram.Bot.Types.Enums.ChatType.Private => "private",
            Telegram.Bot.Types.Enums.ChatType.Group => "group",
            Telegram.Bot.Types.Enums.ChatType.Supergroup => "supergroup",
            Telegram.Bot.Types.Enums.ChatType.Channel => "channel",
            _ => "unknown"
        };
    }

    internal sealed record ObservationBatch(IReadOnlyList<AccountObservation> Accounts, IReadOnlyList<ChatObservation> Chats)
    {
        internal bool IsEmpty => Accounts.Count == 0 && Chats.Count == 0;
    }

    /// <summary>
    /// Collects observations between flushes, one entry per account and per chat, newest on top.
    /// Thread-safe; bounded, so a flood or a PostgreSQL outage cannot grow it without limit.
    /// </summary>
    internal sealed class ObservationBuffer
    {
        // Telegram's shared senders: channel posts (Channel_Bot), anonymous admins
        // (GroupAnonymousBot) and linked-channel forwards ("Telegram"). Not people.
        internal static readonly HashSet<long> Placeholders = new HashSet<long> { 136817688, 1087968824, 777000 };

        private readonly object _lock = new object();
        private readonly int _maxAccounts;
        private readonly int _maxChats;
        private Dictionary<long, AccountObservation> _accounts = new Dictionary<long, AccountObservation>();
        private Dictionary<long, ChatObservation> _chats = new Dictionary<long, ChatObservation>();
        private int _dropped;

        internal ObservationBuffer(int maxAccounts = 200_000, int maxChats = 50_000)
        {
            _maxAccounts = maxAccounts;
            _maxChats = maxChats;
        }

        internal void Add(AccountObservation observation)
        {
            if (Placeholders.Contains(observation.TelegramUserId)) return;
            lock (_lock)
            {
                if (_accounts.TryGetValue(observation.TelegramUserId, out var existing))
                    _accounts[observation.TelegramUserId] = existing.Then(observation);
                else if (_accounts.Count < _maxAccounts)
                    _accounts[observation.TelegramUserId] = observation;
                else
                    _dropped++;
            }
        }

        internal void Add(ChatObservation observation)
        {
            lock (_lock)
            {
                if (_chats.TryGetValue(observation.TelegramChatId, out var existing))
                    _chats[observation.TelegramChatId] = existing.Then(observation);
                else if (_chats.Count < _maxChats)
                    _chats[observation.TelegramChatId] = observation;
                else
                    _dropped++;
            }
        }

        /// <summary>Everything collected so far, and how many observations the caps dropped; empties the buffer.</summary>
        internal (ObservationBatch Batch, int Dropped) Take()
        {
            lock (_lock)
            {
                var batch = new ObservationBatch(_accounts.Values.ToList(), _chats.Values.ToList());
                var dropped = _dropped;
                _accounts = new Dictionary<long, AccountObservation>();
                _chats = new Dictionary<long, ChatObservation>();
                _dropped = 0;
                return (batch, dropped);
            }
        }

        /// <summary>Puts a batch that could not be written back, under anything observed since.</summary>
        internal void Restore(ObservationBatch batch)
        {
            lock (_lock)
            {
                foreach (var old in batch.Accounts)
                    _accounts[old.TelegramUserId] = _accounts.TryGetValue(old.TelegramUserId, out var newer)
                        ? old.Then(newer) : old;
                foreach (var old in batch.Chats)
                    _chats[old.TelegramChatId] = _chats.TryGetValue(old.TelegramChatId, out var newer)
                        ? old.Then(newer) with { ObservedUserCount = newer.ObservedUserCount, LinkKnown = newer.LinkKnown, PublicLink = newer.PublicLink }
                        : old;
            }
        }

        /// <summary>What one update says about the accounts and chats in it.</summary>
        internal void Collect(Update update)
        {
            if (update.CallbackQuery is { } callback)
            {
                Add(Account(callback.From, DateTimeOffset.UtcNow));
                return;
            }
            if (update.InlineQuery is { } inline)
            {
                Add(Account(inline.From, DateTimeOffset.UtcNow));
                return;
            }
            if (update.ChatMember is { } member)
            {
                // Someone joining, leaving or being let in: the member, whoever did it, and the group.
                var when = new DateTimeOffset(DateTime.SpecifyKind(member.Date, DateTimeKind.Utc));
                if (member.NewChatMember?.User != null) Add(Account(member.NewChatMember.User, null));
                if (member.From != null && member.From.Id != member.NewChatMember?.User?.Id) Add(Account(member.From, null));
                var t = ChatObservation.TypeOf(member.Chat.Type);
                Add(new ChatObservation(member.Chat.Id, t, member.Chat.Title, member.Chat.Username, null, null));
                return;
            }
            var message = update.Message;
            if (message == null) return;
            var at = new DateTimeOffset(DateTime.SpecifyKind(message.Date, DateTimeKind.Utc));

            if (message.From != null) Add(Account(message.From, at));
            if (message.ReplyToMessage?.From != null) Add(Account(message.ReplyToMessage.From, null));
            foreach (var joined in message.NewChatMembers ?? Array.Empty<User>())
                Add(Account(joined, null));
            if (message.LeftChatMember != null) Add(Account(message.LeftChatMember, null));

            var chat = message.Chat;
            var type = ChatObservation.TypeOf(chat.Type);
            bool? active = null;
            if (type is "group" or "supergroup")
                active = !(message.LeftChatMember != null && Helpers.Bot.Me != null
                           && message.LeftChatMember.Id == Helpers.Bot.Me.Id);
            Add(new ChatObservation(chat.Id, type, type == "private" ? null : chat.Title,
                type == "private" ? null : chat.Username, at, active));

            // A post made as a channel (or as a linked channel): the channel is a chat we have seen.
            if (message.SenderChat is { } sender && sender.Id != chat.Id)
                Add(new ChatObservation(sender.Id, ChatObservation.TypeOf(sender.Type), sender.Title,
                    sender.Username, at, null));
        }

        private static AccountObservation Account(User user, DateTimeOffset? seenAt) =>
            new AccountObservation(user.Id, user.IsBot,
                $"{user.FirstName} {user.LastName}".Trim(), user.Username, user.LanguageCode, seenAt);
    }
}
