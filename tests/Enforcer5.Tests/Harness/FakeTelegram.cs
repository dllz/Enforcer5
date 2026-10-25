using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>A member of a simulated chat, as Telegram sees them.</summary>
    internal enum MemberState
    {
        Creator,
        Administrator,
        Member,
        Restricted,
        Left,
        Kicked,
        /// <summary>Telegram has no record: getChatMember answers 400 "member not found".</summary>
        Unknown
    }

    /// <summary>One request the bot made, kept so a scenario can check what Enforcer did.</summary>
    internal sealed record TelegramCall(string Method, long? ChatId, long? UserId, string Text, object Request);

    /// <summary>
    /// Stands in for Telegram. Every Bot API call in Telegram.Bot 22 ends in SendRequest, so this
    /// answers the requests Enforcer makes from simulated chat state, the way Telegram does,
    /// including its refusals: 400 "member not found" for someone Telegram has no record of, "not
    /// enough rights" when the bot is not an admin, "user is an administrator" for banning an admin.
    /// <para>
    /// A request the fake does not know throws, so the scenario's error check names it: add it here
    /// when a new scenario needs it.
    /// </para>
    /// </summary>
    internal sealed class FakeTelegram : ITelegramBotClient
    {
        private readonly ConcurrentDictionary<(long Chat, long User), MemberState> _members = new();
        private readonly ConcurrentDictionary<long, bool> _botCanRestrict = new();
        private readonly ConcurrentQueue<TelegramCall> _calls = new();
        private readonly ConcurrentQueue<Message> _sent = new();
        private int _nextMessageId = 1000;

        internal FakeTelegram(User me)
        {
            Me = me;
        }

        internal User Me { get; }

        internal IReadOnlyList<TelegramCall> Calls => _calls.ToList();

        internal void SetMember(long chatId, long userId, MemberState state) => _members[(chatId, userId)] = state;

        internal MemberState GetMember(long chatId, long userId) =>
            _members.TryGetValue((chatId, userId), out var state) ? state : MemberState.Unknown;

        internal void SetBotCanRestrict(long chatId, bool can) => _botCanRestrict[chatId] = can;

        internal int NextMessageId() => Interlocked.Increment(ref _nextMessageId);

        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object response = request switch
            {
                SendMessageRequest r => Sent(r),
                ForwardMessageRequest r => Record("forwardMessage", r.ChatId, null, null, r, NewMessage(r.ChatId)),
                EditMessageTextRequest r => Record("editMessageText", r.ChatId, null, r.Text, r, NewMessage(r.ChatId, r.Text)),
                DeleteMessageRequest r => Record("deleteMessage", r.ChatId, null, null, r, true),
                AnswerCallbackQueryRequest r => Record("answerCallbackQuery", null, null, r.Text, r, true),
                GetChatMemberRequest r => Record("getChatMember", r.ChatId, r.UserId, null, r, ChatMember(Id(r.ChatId), r.UserId)),
                BanChatMemberRequest r => Record("banChatMember", r.ChatId, r.UserId, null, r, Ban(Id(r.ChatId), r.UserId)),
                UnbanChatMemberRequest r => Record("unbanChatMember", r.ChatId, r.UserId, null, r, Unban(Id(r.ChatId), r.UserId)),
                RestrictChatMemberRequest r => Record("restrictChatMember", r.ChatId, r.UserId, null, r, Restrict(Id(r.ChatId), r.UserId, r.Permissions)),
                _ => throw new ApiRequestException($"Harness: FakeTelegram does not answer {request.GetType().Name} yet", 400)
            };
            return Task.FromResult((TResponse)response);
        }

        private static long Id(ChatId chat) => chat.Identifier ?? 0;

        private T Record<T>(string method, ChatId chat, long? user, string text, object request, T result)
        {
            _calls.Enqueue(new TelegramCall(method, chat?.Identifier, user, text, request));
            return result;
        }

        private Message Sent(SendMessageRequest r)
        {
            var message = NewMessage(r.ChatId, r.Text);
            // Kept so a scenario can press the buttons under it.
            message.ReplyMarkup = r.ReplyMarkup as Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup;
            _sent.Enqueue(message);
            return Record("sendMessage", r.ChatId, null, r.Text, r, message);
        }

        /// <summary>The messages the bot sent, oldest first, with their inline buttons.</summary>
        internal IReadOnlyList<Message> SentMessages => _sent.ToList();

        private Message NewMessage(ChatId chat, string text = null) => new Message
        {
            Id = NextMessageId(),
            Chat = new Chat { Id = Id(chat), Type = Id(chat) < 0 ? ChatType.Supergroup : ChatType.Private },
            From = Me,
            Date = DateTime.UtcNow,
            Text = text
        };

        private ChatMember ChatMember(long chatId, long userId)
        {
            var user = new User { Id = userId, FirstName = $"User{userId}", IsBot = userId == Me.Id };
            if (userId == Me.Id)
                return new ChatMemberAdministrator { User = user, CanRestrictMembers = BotCanRestrict(chatId), CanDeleteMessages = true };
            return GetMember(chatId, userId) switch
            {
                MemberState.Creator => new ChatMemberOwner { User = user },
                MemberState.Administrator => new ChatMemberAdministrator { User = user, CanRestrictMembers = true },
                MemberState.Member => new ChatMemberMember { User = user },
                MemberState.Restricted => new ChatMemberRestricted { User = user, IsMember = true },
                MemberState.Left => new ChatMemberLeft { User = user },
                MemberState.Kicked => new ChatMemberBanned { User = user },
                _ => throw new ApiRequestException("Bad Request: member not found", 400)
            };
        }

        private bool BotCanRestrict(long chatId) => !_botCanRestrict.TryGetValue(chatId, out var can) || can;

        private bool Ban(long chatId, long userId)
        {
            if (!BotCanRestrict(chatId))
                throw new ApiRequestException("Bad Request: not enough rights to restrict/unrestrict chat member", 400);
            var state = GetMember(chatId, userId);
            if (state is MemberState.Creator or MemberState.Administrator)
                throw new ApiRequestException("Bad Request: user is an administrator of the chat", 400);
            SetMember(chatId, userId, MemberState.Kicked);
            return true;
        }

        private bool Restrict(long chatId, long userId, ChatPermissions permissions)
        {
            if (!BotCanRestrict(chatId))
                throw new ApiRequestException("Bad Request: not enough rights to restrict/unrestrict chat member", 400);
            var state = GetMember(chatId, userId);
            if (state is MemberState.Creator or MemberState.Administrator)
                throw new ApiRequestException("Bad Request: user is an administrator of the chat", 400);
            if (state is MemberState.Member or MemberState.Restricted)
                SetMember(chatId, userId, permissions?.CanSendMessages == true ? MemberState.Member : MemberState.Restricted);
            return true;
        }

        private bool Unban(long chatId, long userId)
        {
            if (!BotCanRestrict(chatId))
                throw new ApiRequestException("Bad Request: not enough rights to restrict/unrestrict chat member", 400);
            if (GetMember(chatId, userId) == MemberState.Kicked)
                SetMember(chatId, userId, MemberState.Left);
            return true;
        }

        // The rest of ITelegramBotClient, unused by Enforcer.
        public bool LocalBotServer => false;
        public long BotId => Me.Id;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
        public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();
        public event AsyncEventHandler<ApiRequestEventArgs> OnMakingApiRequest { add { } remove { } }
        public event AsyncEventHandler<ApiResponseEventArgs> OnApiResponseReceived { add { } remove { } }
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Harness: no file downloads");
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Harness: no file downloads");
    }
}
