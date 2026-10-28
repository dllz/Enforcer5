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

    /// <summary>
    /// One request the bot made, kept so a scenario can check what Enforcer did. <see cref="At"/> is
    /// the simulated time; <see cref="RetryAfter"/> is set when Telegram answered 429.
    /// </summary>
    internal sealed record TelegramCall(string Method, long? ChatId, long? UserId, string Text, object Request)
    {
        internal DateTimeOffset At { get; init; }
        internal int? RetryAfter { get; init; }
        /// <summary>The 429 came from an injected storm, not from going over a limit.</summary>
        internal bool Injected { get; init; }
        internal bool Refused => RetryAfter.HasValue;
        /// <summary>Telegram's error, when it refused the call for another reason than a 429.</summary>
        internal string Error { get; init; }
        /// <summary>Telegram carried it out.</summary>
        internal bool Succeeded => !Refused && Error == null;
    }

    /// <summary>
    /// Telegram's limits as the harness enforces them. Going over answers 429 with the
    /// <c>retry_after</c> until a slot frees, exactly as a scenario would see it in production.
    /// The exact numbers Telegram applies are not published; these follow its documentation
    /// (one message a second per chat with short bursts, 20 a minute per group, about 30 a second
    /// per bot) and are what Enforcer's own pacing has to stay under.
    /// </summary>
    internal sealed class TelegramLimits
    {
        internal bool Enabled = true;
        internal int ChatMessages = 3;
        internal TimeSpan ChatWindow = TimeSpan.FromSeconds(3);
        internal int GroupMessages = 20;
        internal TimeSpan GroupWindow = TimeSpan.FromMinutes(1);
        internal int BotMessages = 30;
        internal TimeSpan BotWindow = TimeSpan.FromSeconds(1);
        /// <summary>Bans, restrictions, deletes and permission changes, bot-wide.</summary>
        internal int BotModeration = 30;
        internal TimeSpan ModerationWindow = TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Stands in for Telegram. Every Bot API call in Telegram.Bot 22 ends in SendRequest, so this
    /// answers the requests Enforcer makes from simulated chat state, the way Telegram does,
    /// including its refusals: 400 "member not found" for someone Telegram has no record of, "not
    /// enough rights" when the bot is not an admin, "user is an administrator" for banning an admin,
    /// and 429 when the bot goes over <see cref="Limits"/> or a scenario injects a storm.
    /// <para>
    /// A request the fake does not know throws, so the scenario's error check names it: add it here
    /// when a new scenario needs it.
    /// </para>
    /// </summary>
    internal sealed class FakeTelegram : ITelegramBotClient
    {
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<(long Chat, long User), MemberState> _members = new();
        private readonly ConcurrentDictionary<long, bool> _botCanRestrict = new();
        private readonly ConcurrentQueue<TelegramCall> _calls = new();
        private readonly ConcurrentQueue<Message> _sent = new();
        // Messages members posted that are still in the chat: (chat, message id) -> sender.
        private readonly Dictionary<(long Chat, int Message), long> _posted = new();
        private readonly Dictionary<long, ChatPermissions> _permissions = new();
        private readonly HashSet<(long Chat, long SenderChat)> _bannedSenderChats = new();
        private readonly Dictionary<string, List<DateTimeOffset>> _windows = new();
        private readonly List<Storm> _storms = new();
        private int _nextMessageId = 1000;
        private TimeSpan _latency = TimeSpan.Zero;

        private sealed record Storm(Func<TelegramCall, bool> Matches, DateTimeOffset Until, int RetryAfter);

        internal FakeTelegram(User me, FakeClock clock)
        {
            Me = me;
            Clock = clock;
        }

        internal User Me { get; }
        internal FakeClock Clock { get; }
        internal TelegramLimits Limits { get; } = new TelegramLimits();

        internal IReadOnlyList<TelegramCall> Calls => _calls.ToList();

        /// <summary>Calls Telegram carried out (not answered 429 or refused).</summary>
        internal IReadOnlyList<TelegramCall> Accepted => _calls.Where(c => c.Succeeded).ToList();

        /// <summary>429s that came from going over a limit rather than from an injected storm.</summary>
        internal IReadOnlyList<TelegramCall> LimitRefusals => _calls.Where(c => c.Refused && !c.Injected).ToList();

        internal void SetMember(long chatId, long userId, MemberState state) => _members[(chatId, userId)] = state;

        internal MemberState GetMember(long chatId, long userId) =>
            _members.TryGetValue((chatId, userId), out var state) ? state : MemberState.Unknown;

        internal void SetBotCanRestrict(long chatId, bool can) => _botCanRestrict[chatId] = can;

        internal int NextMessageId() => Interlocked.Increment(ref _nextMessageId);

        /// <summary>Every call answered after this many simulated seconds; zero by default.</summary>
        internal void SetLatency(TimeSpan latency) => _latency = latency;

        private readonly ConcurrentDictionary<string, TimeSpan> _methodLatency = new();

        /// <summary>Only <paramref name="method"/> (e.g. "getChat") answered after <paramref name="latency"/>.</summary>
        internal void SetLatency(string method, TimeSpan latency) => _methodLatency[method] = latency;

        /// <summary>
        /// Answers 429 with <paramref name="retryAfter"/> to every call <paramref name="matches"/>
        /// accepts, for <paramref name="duration"/> of simulated time from now.
        /// </summary>
        internal void StartStorm(Func<TelegramCall, bool> matches, TimeSpan duration, int retryAfter)
        {
            lock (_lock) _storms.Add(new Storm(matches, Clock.GetUtcNow() + duration, retryAfter));
        }

        /// <summary>A storm on everything that posts or edits a message.</summary>
        internal void StartMessageStorm(TimeSpan duration, int retryAfter = 30) =>
            StartStorm(c => IsMessage(c.Method), duration, retryAfter);

        /// <summary>A storm on every call.</summary>
        internal void StartFullStorm(TimeSpan duration, int retryAfter = 30) => StartStorm(_ => true, duration, retryAfter);

        /// <summary>A member's message, so the scenario can check it is deleted later.</summary>
        internal void Posted(long chatId, int messageId, long userId)
        {
            lock (_lock) _posted[(chatId, messageId)] = userId;
        }

        /// <summary>Messages from <paramref name="userId"/> still in the chat.</summary>
        internal IReadOnlyList<int> MessagesFrom(long chatId, long userId)
        {
            lock (_lock) return _posted.Where(p => p.Key.Chat == chatId && p.Value == userId).Select(p => p.Key.Message).ToList();
        }

        /// <summary>The chat's own permissions, as set with setChatPermissions; null when never set.</summary>
        internal ChatPermissions ChatPermissions(long chatId)
        {
            lock (_lock) return _permissions.TryGetValue(chatId, out var p) ? p : null;
        }

        internal void SetChatPermissions(long chatId, ChatPermissions permissions)
        {
            lock (_lock) _permissions[chatId] = permissions;
        }

        internal bool SenderChatBanned(long chatId, long senderChatId)
        {
            lock (_lock) return _bannedSenderChats.Contains((chatId, senderChatId));
        }

        public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (_latency > TimeSpan.Zero) await Enforcer5.Helpers.Clock.Delay(_latency);
            if (_methodLatency.TryGetValue(request.MethodName, out var slow) && slow > TimeSpan.Zero)
                await Enforcer5.Helpers.Clock.Delay(slow);
            return (TResponse)Answer(request);
        }

        private object Answer<TResponse>(IRequest<TResponse> request)
        {
            switch (request)
            {
                case SendMessageRequest r: return Limited("sendMessage", r.ChatId, null, r.Text, r, () => Sent(r));
                case SendDocumentRequest r: return Limited("sendDocument", r.ChatId, null, (r.Document as InputFileId)?.Id, r, () => NewMessage(r.ChatId, r.Caption));
                case SendPhotoRequest r: return Limited("sendPhoto", r.ChatId, null, r.Caption, r, () => NewMessage(r.ChatId, r.Caption));
                case ForwardMessageRequest r: return Limited("forwardMessage", r.ChatId, null, null, r, () => NewMessage(r.ChatId));
                case EditMessageTextRequest r: return Limited("editMessageText", r.ChatId, null, r.Text, r, () => Edited(r));
                case EditEphemeralMessageTextRequest r: return Limited("editEphemeralMessageText", r.ChatId, r.ReceiverUserId, r.Text, r, () => EditEphemeral(r));
                case DeleteEphemeralMessageRequest r: return Limited("deleteEphemeralMessage", r.ChatId, r.ReceiverUserId, null, r, () => DeleteEphemeral(r));
                case EditMessageReplyMarkupRequest r: return Limited("editMessageReplyMarkup", r.ChatId, null, null, r, () => NewMessage(r.ChatId));
                case DeleteMessageRequest r: return Limited("deleteMessage", r.ChatId, null, null, r, () => Delete(Id(r.ChatId), new[] { r.MessageId }));
                case DeleteMessagesRequest r: return Limited("deleteMessages", r.ChatId, null, null, r, () => Delete(Id(r.ChatId), r.MessageIds.ToArray()));
                case AnswerCallbackQueryRequest r: return Limited("answerCallbackQuery", null, null, r.Text, r, () => true);
                case AnswerInlineQueryRequest r: return Limited("answerInlineQuery", null, null, null, r, () => true);
                case GetChatAdministratorsRequest r: return Limited("getChatAdministrators", r.ChatId, null, null, r, () => Administrators(Id(r.ChatId)));
                case GetChatMemberRequest r: return Limited("getChatMember", r.ChatId, r.UserId, null, r, () => ChatMember(Id(r.ChatId), r.UserId));
                case GetChatRequest r: return Limited("getChat", r.ChatId, null, null, r, () => ChatInfo(r.ChatId));
                case BanChatMemberRequest r: return Limited("banChatMember", r.ChatId, r.UserId, null, r, () => Ban(Id(r.ChatId), r.UserId, r.RevokeMessages));
                case UnbanChatMemberRequest r: return Limited("unbanChatMember", r.ChatId, r.UserId, null, r, () => Unban(Id(r.ChatId), r.UserId, r.OnlyIfBanned == true));
                case RestrictChatMemberRequest r: return Limited("restrictChatMember", r.ChatId, r.UserId, null, r, () => Restrict(Id(r.ChatId), r.UserId, r.Permissions));
                case SetChatPermissionsRequest r: return Limited("setChatPermissions", r.ChatId, null, null, r, () => SetPermissions(Id(r.ChatId), r.Permissions));
                case BanChatSenderChatRequest r: return Limited("banChatSenderChat", r.ChatId, r.SenderChatId, null, r, () => BanSenderChat(Id(r.ChatId), r.SenderChatId, true));
                case UnbanChatSenderChatRequest r: return Limited("unbanChatSenderChat", r.ChatId, r.SenderChatId, null, r, () => BanSenderChat(Id(r.ChatId), r.SenderChatId, false));
                case LeaveChatRequest r: return Limited("leaveChat", r.ChatId, null, null, r, () => true);
                case GetMyCommandsRequest r: return Limited("getMyCommands", null, null, null, r, () => GetCommands(r.Scope, r.LanguageCode));
                case SetMyCommandsRequest r: return Limited("setMyCommands", null, null, null, r, () => SetCommands(r.Scope, r.LanguageCode, r.Commands.ToArray()));
                case DeleteMyCommandsRequest r: return Limited("deleteMyCommands", null, null, null, r, () => SetCommands(r.Scope, r.LanguageCode, null));
                default:
                    throw new ApiRequestException($"Harness: FakeTelegram does not answer {request.GetType().Name} yet", 400);
            }
        }

        private static long Id(ChatId chat) => chat?.Identifier ?? 0;

        private readonly ConcurrentDictionary<string, BotCommand[]> _commandMenus = new();

        /// <summary>The menu set for a scope and language ("" for the default), keyed like "AllGroupChats" or "Chat:123".</summary>
        internal static string MenuKey(BotCommandScope scope, string languageCode) =>
            (scope is BotCommandScopeChat chat ? $"Chat:{chat.ChatId.Identifier}" : (scope?.Type ?? BotCommandScopeType.Default).ToString())
            + "/" + (languageCode ?? "");

        /// <summary>Every menu the bot has set, by <see cref="MenuKey"/>.</summary>
        internal IReadOnlyDictionary<string, BotCommand[]> CommandMenus => _commandMenus;

        /// <summary>Sets a menu as if from BotFather, for a scenario to start from.</summary>
        internal void PresetCommands(BotCommandScope scope, string languageCode, params BotCommand[] commands) =>
            _commandMenus[MenuKey(scope, languageCode)] = commands;

        private BotCommand[] GetCommands(BotCommandScope scope, string languageCode) =>
            _commandMenus.TryGetValue(MenuKey(scope, languageCode), out var commands) ? commands : Array.Empty<BotCommand>();

        private bool SetCommands(BotCommandScope scope, string languageCode, BotCommand[] commands)
        {
            if (commands == null || commands.Length == 0) _commandMenus.TryRemove(MenuKey(scope, languageCode), out _);
            else _commandMenus[MenuKey(scope, languageCode)] = commands;
            return true;
        }

        internal static bool IsMessage(string method) =>
            method.StartsWith("send", StringComparison.Ordinal) || method.StartsWith("edit", StringComparison.Ordinal) ||
            method.StartsWith("forward", StringComparison.Ordinal) || method.StartsWith("copy", StringComparison.Ordinal);

        internal static bool IsModeration(string method) => method is "banChatMember" or "unbanChatMember"
            or "restrictChatMember" or "setChatPermissions" or "deleteMessage" or "deleteMessages"
            or "banChatSenderChat" or "unbanChatSenderChat";

        // Records the call, answers 429 when a storm or a limit says so, otherwise runs the answer.
        private T Limited<T>(string method, ChatId chat, long? user, string text, object request, Func<T> answer)
        {
            var call = new TelegramCall(method, chat?.Identifier, user, text, request) { At = Clock.GetUtcNow() };
            int? retryAfter = null;
            var injected = false;
            lock (_lock)
            {
                var now = Clock.GetUtcNow();
                _storms.RemoveAll(s => s.Until <= now);
                var storm = _storms.FirstOrDefault(s => s.Matches(call));
                if (storm != null)
                {
                    retryAfter = Math.Max(1, Math.Min(storm.RetryAfter, (int)Math.Ceiling((storm.Until - now).TotalSeconds)));
                    injected = true;
                }
                else if (Limits.Enabled)
                {
                    retryAfter = CheckLimits(method, call.ChatId ?? 0, now);
                }
            }
            if (retryAfter.HasValue)
            {
                _calls.Enqueue(call with { RetryAfter = retryAfter, Injected = injected });
                throw new ApiRequestException($"Too Many Requests: retry after {retryAfter}", 429,
                    new ResponseParameters { RetryAfter = retryAfter });
            }
            try
            {
                var result = answer();
                _calls.Enqueue(call);
                return result;
            }
            catch (ApiRequestException e)
            {
                // Telegram said no (not enough rights, user is an admin, ...): not carried out.
                _calls.Enqueue(call with { Error = e.Message });
                throw;
            }
        }

        // Called with the lock held. Null when the call is within every limit (and counts it).
        private int? CheckLimits(string method, long chatId, DateTimeOffset now)
        {
            var windows = new List<(string Key, int Max, TimeSpan Window)>();
            if (IsMessage(method))
            {
                windows.Add(($"chat:{chatId}", Limits.ChatMessages, Limits.ChatWindow));
                if (chatId < 0) windows.Add(($"group:{chatId}", Limits.GroupMessages, Limits.GroupWindow));
                windows.Add(("bot", Limits.BotMessages, Limits.BotWindow));
            }
            else if (IsModeration(method))
            {
                windows.Add(("moderation", Limits.BotModeration, Limits.ModerationWindow));
            }
            else
            {
                return null;
            }

            TimeSpan? wait = null;
            foreach (var (key, max, window) in windows)
            {
                if (!_windows.TryGetValue(key, out var times)) _windows[key] = times = new List<DateTimeOffset>();
                times.RemoveAll(t => t <= now - window);
                if (times.Count >= max)
                {
                    var free = times[times.Count - max] + window - now;
                    if (wait == null || free > wait) wait = free;
                }
            }
            if (wait.HasValue) return Math.Max(1, (int)Math.Ceiling(wait.Value.TotalSeconds));
            foreach (var (key, _, _) in windows) _windows[key].Add(now);
            return null;
        }

        private readonly ConcurrentDictionary<long, byte> _notStarted = new();
        private readonly ConcurrentDictionary<long, string> _titles = new();

        /// <summary>A user who never started the bot: messages to their private chat are refused (403).</summary>
        internal void SetNotStarted(long userId, bool notStarted = true)
        {
            if (notStarted) _notStarted[userId] = 0;
            else _notStarted.TryRemove(userId, out _);
        }

        /// <summary>The title getChat gives a group.</summary>
        internal void SetTitle(long chatId, string title) => _titles[chatId] = title;

        private Message Sent(SendMessageRequest r)
        {
            if (r.ChatId.Identifier is long to && to > 0 && _notStarted.ContainsKey(to))
                throw new ApiRequestException("Forbidden: bot can't initiate conversation with a user", 403);
            if (r.EphemeralMessageParameters is { } ephemeral)
                return SentEphemeral(r, ephemeral);
            var message = NewMessage(r.ChatId, r.Text);
            // Kept so a scenario can press the buttons under it.
            message.ReplyMarkup = r.ReplyMarkup as Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup;
            _sent.Enqueue(message);
            return message;
        }

        private readonly ConcurrentDictionary<(long Chat, long Receiver, int Id), Message> _ephemeral = new();

        // An ephemeral message: only its receiver sees it, and it has no ordinary message id.
        private Message SentEphemeral(SendMessageRequest r, EphemeralMessageParameters ephemeral)
        {
            var message = NewMessage(r.ChatId, r.Text);
            message.EphemeralMessageId = message.Id;
            message.Id = 0;
            message.ReplyMarkup = r.ReplyMarkup as Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup;
            _ephemeral[(Id(r.ChatId), ephemeral.ReceiverUserId, message.EphemeralMessageId.Value)] = message;
            return message;
        }

        private bool EditEphemeral(EditEphemeralMessageTextRequest r)
        {
            if (!_ephemeral.TryGetValue((Id(r.ChatId), r.ReceiverUserId, r.EphemeralMessageId), out var message))
                throw new ApiRequestException("Bad Request: message to edit not found", 400);
            message.Text = r.Text;
            message.ReplyMarkup = r.ReplyMarkup;
            return true;
        }

        private bool DeleteEphemeral(DeleteEphemeralMessageRequest r)
        {
            if (!_ephemeral.TryRemove((Id(r.ChatId), r.ReceiverUserId, r.EphemeralMessageId), out _))
                throw new ApiRequestException("Bad Request: message to delete not found", 400);
            return true;
        }

        /// <summary>The ephemeral messages <paramref name="receiverId"/> still sees in <paramref name="chatId"/>, oldest first.</summary>
        internal IReadOnlyList<Message> EphemeralFor(long chatId, long receiverId) =>
            _ephemeral.Where(e => e.Key.Chat == chatId && e.Key.Receiver == receiverId)
                .OrderBy(e => e.Key.Id).Select(e => e.Value).ToList();

        // An edit changes the message the scenario holds, as a client would show it.
        private Message Edited(EditMessageTextRequest r)
        {
            var message = _sent.FirstOrDefault(m => m.Chat.Id == Id(r.ChatId) && m.Id == r.MessageId);
            if (message == null) return NewMessage(r.ChatId, r.Text);
            message.Text = r.Text;
            message.ReplyMarkup = r.ReplyMarkup;
            return message;
        }

        /// <summary>The messages the bot sent, oldest first, with their inline buttons.</summary>
        internal IReadOnlyList<Message> SentMessages => _sent.ToList();

        private Message NewMessage(ChatId chat, string text = null) => new Message
        {
            Id = NextMessageId(),
            Chat = new Chat { Id = Id(chat), Type = Id(chat) < 0 ? ChatType.Supergroup : ChatType.Private },
            From = Me,
            Date = Clock.UtcNow,
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

        private ChatMember[] Administrators(long chatId) =>
            new ChatMember[] { new ChatMemberAdministrator { User = Me, CanRestrictMembers = BotCanRestrict(chatId), CanDeleteMessages = true } }
                .Concat(_members.Where(m => m.Key.Chat == chatId && m.Key.User != Me.Id
                        && m.Value is MemberState.Creator or MemberState.Administrator)
                    .Select(m => ChatMember(chatId, m.Key.User)))
                .ToArray();

        /// <summary>Everyone Telegram has as an admin or the creator of the chat, the bot left out.</summary>
        internal IReadOnlyList<long> AdminIds(long chatId) =>
            _members.Where(m => m.Key.Chat == chatId && m.Key.User != Me.Id && m.Value is MemberState.Creator or MemberState.Administrator)
                .Select(m => m.Key.User).ToList();

        private readonly ConcurrentDictionary<string, (long Id, ChatType Type)> _publicChats = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<long, long> _linkedChannels = new();

        /// <summary>A public channel or group elsewhere that getChat("@name") finds.</summary>
        internal void AddPublicChat(string username, long id, ChatType type = ChatType.Channel) => _publicChats[username.TrimStart('@')] = (id, type);

        /// <summary>The channel linked to a group.</summary>
        internal void SetLinkedChannel(long chatId, long channelId) => _linkedChannels[chatId] = channelId;

        private ChatFullInfo ChatInfo(ChatId chat)
        {
            if (chat.Username == null) return ChatInfo(Id(chat));
            if (!_publicChats.TryGetValue(chat.Username.TrimStart('@'), out var found))
                throw new ApiRequestException("Bad Request: chat not found", 400);
            return new ChatFullInfo { Id = found.Id, Type = found.Type, Username = chat.Username.TrimStart('@') };
        }

        private ChatFullInfo ChatInfo(long chatId) => new ChatFullInfo
        {
            Id = chatId,
            Title = _titles.TryGetValue(chatId, out var title) ? title : null,
            LinkedChatId = _linkedChannels.TryGetValue(chatId, out var linked) ? linked : null,
            Type = chatId < 0 ? ChatType.Supergroup : ChatType.Private,
            Permissions = ChatPermissions(chatId) ?? new ChatPermissions
            {
                CanSendMessages = true, CanSendAudios = true, CanSendDocuments = true, CanSendPhotos = true,
                CanSendVideos = true, CanSendVideoNotes = true, CanSendVoiceNotes = true, CanSendPolls = true,
                CanSendOtherMessages = true, CanAddWebPagePreviews = true, CanInviteUsers = true
            }
        };

        /// <summary>The bot's last message to <paramref name="chatId"/> (a group, or an admin's private chat).</summary>
        internal Message LastMessageTo(long chatId) => SentMessages.Last(m => m.Chat.Id == chatId);

        private bool BotCanRestrict(long chatId) => !_botCanRestrict.TryGetValue(chatId, out var can) || can;

        private void RequireRights(long chatId)
        {
            if (!BotCanRestrict(chatId))
                throw new ApiRequestException("Bad Request: not enough rights to restrict/unrestrict chat member", 400);
        }

        private bool Delete(long chatId, int[] messageIds)
        {
            if (!BotCanRestrict(chatId))
                throw new ApiRequestException("Bad Request: message can't be deleted", 400);
            lock (_lock)
            {
                foreach (var id in messageIds) _posted.Remove((chatId, id));
            }
            return true;
        }

        private bool Ban(long chatId, long userId, bool revokeMessages)
        {
            RequireRights(chatId);
            var state = GetMember(chatId, userId);
            if (state is MemberState.Creator or MemberState.Administrator)
                throw new ApiRequestException("Bad Request: user is an administrator of the chat", 400);
            SetMember(chatId, userId, MemberState.Kicked);
            if (revokeMessages)
            {
                lock (_lock)
                {
                    foreach (var key in _posted.Where(p => p.Key.Chat == chatId && p.Value == userId).Select(p => p.Key).ToList())
                        _posted.Remove(key);
                }
            }
            return true;
        }

        private bool Restrict(long chatId, long userId, ChatPermissions permissions)
        {
            RequireRights(chatId);
            var state = GetMember(chatId, userId);
            if (state is MemberState.Creator or MemberState.Administrator)
                throw new ApiRequestException("Bad Request: user is an administrator of the chat", 400);
            if (state is MemberState.Member or MemberState.Restricted)
                SetMember(chatId, userId, permissions?.CanSendMessages == true ? MemberState.Member : MemberState.Restricted);
            return true;
        }

        private bool Unban(long chatId, long userId, bool onlyIfBanned)
        {
            RequireRights(chatId);
            var state = GetMember(chatId, userId);
            if (state is MemberState.Creator or MemberState.Administrator)
                throw new ApiRequestException("Bad Request: user is an administrator of the chat", 400);
            // Without only_if_banned Telegram also removes a current member, which is how a kick
            // is done in two calls.
            if (state == MemberState.Kicked || (!onlyIfBanned && state is MemberState.Member or MemberState.Restricted))
                SetMember(chatId, userId, MemberState.Left);
            return true;
        }

        private bool SetPermissions(long chatId, ChatPermissions permissions)
        {
            RequireRights(chatId);
            SetChatPermissions(chatId, permissions);
            return true;
        }

        private bool BanSenderChat(long chatId, long senderChatId, bool ban)
        {
            RequireRights(chatId);
            lock (_lock)
            {
                if (ban) _bannedSenderChats.Add((chatId, senderChatId));
                else _bannedSenderChats.Remove((chatId, senderChatId));
            }
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
