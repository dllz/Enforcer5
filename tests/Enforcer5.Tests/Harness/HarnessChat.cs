using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Handlers;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// A simulated group chat for Enforcer: messages go through the real update path
    /// (UpdateHandler.RouteAsync, command dispatch, the command code) against a fake Telegram, so a
    /// scenario checks what the bot replied, which Telegram calls it made, what it wrote to
    /// PostgreSQL and whether anything was reported as an error.
    /// <para>
    /// Time is simulated (<see cref="FakeClock"/>): it stands still while an update is handled, and
    /// only moves when the scenario calls <see cref="AdvanceAsync"/>, which fires every timer due on
    /// the way and waits for what each one sets off. Telegram enforces its limits on that clock and
    /// answers 429 when the bot goes over them (<see cref="FakeTelegram.Limits"/>), or when a scenario
    /// starts a storm.
    /// </para>
    /// <para>
    /// Redis is a loose stand-in that answers reads with "nothing stored" and drops writes, except for
    /// hashes (<see cref="FakeRedisHashes"/>) and strings, sets and sorted sets
    /// (<see cref="FakeRedisKeys"/>), which keep their values, with expiry on the simulated clock.
    /// PostgreSQL is a throwaway schema on the test database (ENFORCER_TEST_POSTGRES); without one the
    /// scenarios are skipped.
    /// </para>
    /// <para>
    /// The bot is static throughout, so a harness takes over Bot.Api, Redis.db, the clock and
    /// PostgresStorage for its lifetime and gives them back on Dispose. Scenarios must not run in
    /// parallel. <see cref="Group"/> adds more chats to the same simulated bot.
    /// </para>
    /// </summary>
    internal sealed class HarnessChat : IAsyncDisposable
    {
        internal static readonly User BotUser = new User { Id = 777000111, IsBot = true, FirstName = "Enforcer", Username = "EnforcerHarnessBot" };

        private readonly World _world;
        private readonly bool _owner;

        /// <summary>What every chat of one harness shares: the bot, Telegram, Redis, PostgreSQL, the clock.</summary>
        private sealed class World
        {
            internal readonly ConcurrentQueue<string> Errors = new();
            internal Action<string> PreviousObserver;
            internal Telegram.Bot.ITelegramBotClient PreviousApi;
            internal User PreviousMe;
            internal string ConnectionString;
            internal PostgresDatabase Database;
            internal int NextUpdateId = 1;
            internal FakeClock Clock;
            internal FakeTelegram Telegram;
            internal Mock<IDatabase> Redis = new Mock<IDatabase>();
            internal FakeRedisHashes Hashes = new FakeRedisHashes();
            internal FakeRedisKeys Keys;
            internal System.Threading.CancellationTokenSource QueueStop = new();
            internal FakeFiles Files = new FakeFiles();
            internal ITelegramFileDownloader PreviousFiles;
        }

        internal long ChatId { get; }
        internal string Title { get; set; }
        /// <summary>The group's public username, null for a private group.</summary>
        internal string Username { get; set; }
        internal FakeTelegram Telegram => _world.Telegram;
        internal Mock<IDatabase> Redis => _world.Redis;
        internal FakeClock Clock => _world.Clock;

        /// <summary>The Redis hashes, to seed settings and check counters.</summary>
        internal FakeRedisHashes Hashes => _world.Hashes;

        /// <summary>The Redis strings, sets and sorted sets.</summary>
        internal FakeRedisKeys Keys => _world.Keys;

        /// <summary>Files members sent, as the bot downloads them.</summary>
        internal FakeFiles Files => _world.Files;

        /// <summary>Everything ErrorReporter saw while the harness ran.</summary>
        internal IReadOnlyList<string> Errors => _world.Errors.ToList();

        /// <summary>The database the scenario's PostgreSQL writes went to.</summary>
        internal PostgresDatabase Database => _world.Database;

        private HarnessChat(World world, long chatId, string title, bool owner)
        {
            _world = world;
            ChatId = chatId;
            Title = title;
            _owner = owner;
            world.Telegram.SetTitle(chatId, title);
        }

        /// <summary>A new supergroup with a fresh PostgreSQL schema. Skips the test without a database.</summary>
        internal static async Task<HarnessChat> StartAsync(long chatId = -1001234567890, string title = "Harness group",
            string username = null)
        {
            var clock = FakeClock.StartingNow();
            var world = new World
            {
                Clock = clock,
                Telegram = new FakeTelegram(BotUser, clock),
                PreviousApi = Bot.Api,
                PreviousMe = Bot.Me,
                PreviousObserver = ErrorReporter.Observer
            };
            world.Keys = new FakeRedisKeys(clock, world.Hashes);
            var chat = new HarnessChat(world, chatId, title, owner: true);
            try
            {
                await chat.SetUpAsync();
                chat.Username = username;
                return chat;
            }
            catch
            {
                await chat.DisposeAsync();
                throw;
            }
        }

        /// <summary>Another group served by the same simulated bot.</summary>
        internal HarnessChat Group(long chatId, string title = "Another group", string username = null)
        {
            var chat = new HarnessChat(_world, chatId, title, owner: false) { Username = username };
            Telegram.SetMember(chatId, BotUser.Id, MemberState.Administrator);
            return chat;
        }

        private async Task SetUpAsync()
        {
            _world.ConnectionString = await Storage.TestPostgres.MigratedSchemaAsync();
            // Pooled, a few connections, closed when the database is disposed at the end: opening an
            // unpooled connection to subprod costs a few hundred milliseconds, and a raid writes
            // hundreds of history rows.
            _world.Database = new PostgresDatabase(new Npgsql.NpgsqlConnectionStringBuilder(_world.ConnectionString)
                { Pooling = true, MinPoolSize = 0, MaxPoolSize = 4 }.ConnectionString);
            PostgresStorage.UseForTests(_world.Database);
            // Global bans run from memory, loaded from PostgreSQL (empty here) as at startup.
            GlobalBans.ResetForTests();
            await GlobalBans.ReloadAsync();

            Enforcer5.Helpers.Clock.UseForTests(Clock);
            Enforcer5.Detection.DetectionSettings.ResetForTests();
            Enforcer5.Detection.DetectionEngine.ResetForTests();
            Hashes.Install(Redis);
            Keys.Install(Redis);
#if PREMIUM
            // The premium edition only serves groups on its list.
            Redis.Setup(r => r.SetContainsAsync(It.Is<RedisKey>(k => k == "premiumBot"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
#endif
            Enforcer5.Helpers.Redis.UseForTests(Redis.Object);
            Bot.UseClient(Telegram);
            _world.PreviousFiles = Bot.Files;
            Bot.Files = Files;
            Announcements.ResetForTests();
            CommandGate.ResetForTests();
            FloodTracker.ResetForTests();
            MemberEvents.ResetForTests();
            MemberTrust.SetNormsForTests(null);
            Enforcer5.Raids.RaidTelemetry.ResetForTests();
            Bot.Me = BotUser;
            Bot.StartTime = Clock.UtcNow.AddMinutes(-1);
            Bot.BindHandlers();
            if (!Program.LangaugeList.Any())
                Methods.IntialiseLanguages();
            ErrorReporter.Observer = report => _world.Errors.Enqueue(report);
            Telegram.SetMember(ChatId, BotUser.Id, MemberState.Administrator);
            Enforcer5.Enforcement.EnforcementQueue.ResetForTests();
            Enforcer5.Enforcement.EnforcementQueue.Start(_world.QueueStop.Token);
            Enforcer5.Raids.RaidGuard.ResetForTests();
            Enforcer5.Raids.RaidGuard.Start(_world.QueueStop.Token);
            await SettleAsync();
        }

        /// <summary>
        /// The bot process dies and starts again: the enforcement queue loses everything in memory
        /// and reloads its journal from Redis.
        /// </summary>
        internal async Task CrashAndRestartAsync()
        {
            _world.QueueStop.Cancel();
            Enforcer5.Enforcement.EnforcementQueue.CrashForTests();
            Enforcer5.Raids.RaidGuard.ResetForTests();
            // What a group's settings were in memory, and the members' scores; the Redis cache and
            // PostgreSQL keep the rest.
            Enforcer5.Detection.DetectionSettings.ResetForTests();
            Enforcer5.Detection.DetectionEngine.ResetForTests();
            await SettleAsync();
            _world.QueueStop = new System.Threading.CancellationTokenSource();
            Enforcer5.Enforcement.EnforcementQueue.Start(_world.QueueStop.Token);
            Enforcer5.Raids.RaidGuard.Start(_world.QueueStop.Token);
            await SettleAsync();
        }

        /// <summary>Adds someone to the chat as Telegram would know them.</summary>
        internal HarnessUser Add(string firstName, MemberState state = MemberState.Member, string username = null, long? id = null,
            string lastName = null, string languageCode = null, bool isBot = false)
        {
            var user = new HarnessUser(id ?? 100000 + Telegram.NextMessageId(), firstName, username, lastName, languageCode, isBot);
            Telegram.SetMember(ChatId, user.Id, state);
            return user;
        }

        private Chat ThisChat => new Chat { Id = ChatId, Type = ChatType.Supergroup, Title = Title, Username = Username };

        /// <summary>A text message from <paramref name="from"/>, not yet sent.</summary>
        internal Message Text(HarnessUser from, string text, Message replyTo = null) => new Message
        {
            Id = Telegram.NextMessageId(),
            Chat = ThisChat,
            From = from.ToUser(),
            Date = Clock.UtcNow,
            Text = text,
            ReplyToMessage = replyTo,
            Entities = text.StartsWith("/")
                ? new[] { new MessageEntity { Type = MessageEntityType.BotCommand, Offset = 0, Length = text.Split(' ')[0].Length } }
                : null
        };

        /// <summary>A photo from <paramref name="from"/>, not yet sent. The same unique id is the same image.</summary>
        internal Message Photo(HarnessUser from, string uniqueId = "harness", string caption = null) => new Message
        {
            Id = Telegram.NextMessageId(),
            Chat = ThisChat,
            From = from.ToUser(),
            Date = Clock.UtcNow,
            Caption = caption,
            Photo = new[] { new PhotoSize { FileId = $"photo{Telegram.NextMessageId()}", FileUniqueId = uniqueId, Width = 1, Height = 1 } }
        };

        /// <summary>A file from <paramref name="from"/>, not yet sent; the bot can download its content.</summary>
        internal Message Document(HarnessUser from, string fileName, byte[] content)
        {
            var fileId = $"doc{Telegram.NextMessageId()}";
            Files.Add(fileId, content);
            return new Message
            {
                Id = Telegram.NextMessageId(),
                Chat = ThisChat,
                From = from.ToUser(),
                Date = Clock.UtcNow,
                Document = new Document { FileId = fileId, FileUniqueId = fileId, FileName = fileName, FileSize = content.Length }
            };
        }

        /// <summary>
        /// Posts <paramref name="text"/> as <paramref name="from"/> and waits until everything it set
        /// off is done, PostgreSQL writes included. Returns the message, so others can reply to it.
        /// </summary>
        internal Task<Message> SendAsync(HarnessUser from, string text, Message replyTo = null) =>
            PostAsync(Text(from, text, replyTo));

        /// <summary>Sends a message built with <see cref="Text"/> or <see cref="Photo"/>; see <see cref="SendAsync"/>.</summary>
        internal async Task<Message> PostAsync(Message message)
        {
            Deliver(message);
            await CompleteAsync();
            return message;
        }

        /// <summary>
        /// Lets simulated time run until everything dispatched has finished, background loops left
        /// out: messages waiting for their slot under Telegram's limits go out, as they would while
        /// the members typed their next line. At most <paramref name="limit"/> (10 minutes).
        /// </summary>
        internal Task CompleteAsync(TimeSpan? limit = null) => HarnessTime.CompleteAsync(Clock, limit);

        /// <summary>
        /// Hands <paramref name="message"/> to the bot without waiting, as Telegram would deliver it
        /// in a burst. Call <see cref="SettleAsync"/> or <see cref="AdvanceAsync"/> afterwards.
        /// </summary>
        internal void Deliver(Message message)
        {
            message.Date = Clock.UtcNow;
            if (message.From != null && message.NewChatMembers == null)
                Telegram.Posted(ChatId, message.MessageId, message.From.Id);
            Route(new Update { Id = _world.NextUpdateId++, Message = message });
        }

        /// <summary>
        /// <paramref name="user"/> joins the chat (a service message from them); see
        /// <see cref="SendAsync"/>.
        /// </summary>
        internal async Task<Message> JoinAsync(HarnessUser user)
        {
            var message = Join(user);
            Deliver(message);
            await CompleteAsync();
            return message;
        }

        /// <summary>The service message for <paramref name="user"/> joining, not yet sent; marks them a member.</summary>
        internal Message Join(HarnessUser user, HarnessUser addedBy = null)
        {
            Telegram.SetMember(ChatId, user.Id, MemberState.Member);
            return new Message
            {
                Id = Telegram.NextMessageId(),
                Chat = ThisChat,
                From = (addedBy ?? user).ToUser(),
                Date = Clock.UtcNow,
                NewChatMembers = new[] { user.ToUser() }
            };
        }

        /// <summary>
        /// The member update Telegram sends admins when <paramref name="user"/> joins: through
        /// <paramref name="inviteLink"/> (named <paramref name="linkName"/>), added by
        /// <paramref name="addedBy"/>, or neither. Not yet sent; see <see cref="Deliver(Update)"/>.
        /// </summary>
        internal Update MemberJoined(HarnessUser user, string inviteLink = null, string linkName = null, HarnessUser addedBy = null,
            bool viaRequest = false)
        {
            Telegram.SetMember(ChatId, user.Id, MemberState.Member);
            return new Update
            {
                Id = _world.NextUpdateId++,
                ChatMember = new ChatMemberUpdated
                {
                    Chat = ThisChat,
                    From = (addedBy ?? user).ToUser(),
                    Date = Clock.UtcNow,
                    OldChatMember = new ChatMemberLeft { User = user.ToUser() },
                    NewChatMember = new ChatMemberMember { User = user.ToUser() },
                    InviteLink = inviteLink == null ? null : new ChatInviteLink { InviteLink = inviteLink, Name = linkName, Creator = HarnessChat.BotUser },
                    ViaJoinRequest = viaRequest
                }
            };
        }

        /// <summary>Hands any update to the bot without waiting.</summary>
        internal void Deliver(Update update) => Route(update);

        /// <summary>Posts a photo as <paramref name="from"/>; see <see cref="SendAsync"/>.</summary>
        internal Task<Message> SendPhotoAsync(HarnessUser from, string uniqueId = "harness") => PostAsync(Photo(from, uniqueId));

        /// <summary>
        /// Presses the button under <paramref name="message"/> (a message the bot sent) whose
        /// callback data starts with <paramref name="dataPrefix"/>, as <paramref name="from"/>.
        /// </summary>
        internal async Task PressAsync(HarnessUser from, Message message, string dataPrefix)
        {
            var button = message.ReplyMarkup?.InlineKeyboard.SelectMany(row => row)
                .FirstOrDefault(b => b.CallbackData?.StartsWith(dataPrefix, StringComparison.Ordinal) == true);
            Assert.IsNotNull(button, $"No button starting with {dataPrefix} under: {message.Text}");
            await PressDataAsync(from, message, button.CallbackData);
        }

        /// <summary>A button press handed to the bot without waiting, as part of a burst; see <see cref="Deliver"/>.</summary>
        internal void DeliverPress(HarnessUser from, Message message, string data) =>
            Route(new Update
            {
                Id = _world.NextUpdateId++,
                CallbackQuery = new CallbackQuery
                {
                    Id = $"cb{Telegram.NextMessageId()}", From = from.ToUser(), Message = message, ChatInstance = "harness", Data = data
                }
            });

        /// <summary>A button press with exactly <paramref name="data"/>, on <paramref name="message"/>.</summary>
        internal async Task PressDataAsync(HarnessUser from, Message message, string data)
        {
            var query = new CallbackQuery
            {
                Id = $"cb{Telegram.NextMessageId()}",
                From = from.ToUser(),
                Message = message,
                ChatInstance = "harness",
                Data = data
            };
            Route(new Update { Id = _world.NextUpdateId++, CallbackQuery = query });
            await CompleteAsync();
        }

        /// <summary>The bot's last message to this chat, with its buttons.</summary>
        internal Message LastBotMessage => Telegram.SentMessages.Last(m => m.Chat.Id == ChatId);

        /// <summary>The edition's tempban schedule in Redis: unban time (unix) to "chat:user".</summary>
#if PREMIUM
        internal const string TempbanSchedule = "tempbannedPremium";
#else
        internal const string TempbanSchedule = "tempbanned";
#endif

        /// <summary>
        /// Moves every tempban in the schedule into the past and runs the check the bot's timer
        /// runs, then waits for what it set off.
        /// </summary>
        internal async Task ExpireTempbansAsync()
        {
            var due = 1;
            foreach (var entry in Hashes.All(TempbanSchedule))
            {
                Hashes[TempbanSchedule, entry.Key] = null;
                Hashes[TempbanSchedule, (due++).ToString()] = entry.Value;
            }
            Bot.Dispatch(() => { Methods.CheckTempBans(null); return Task.CompletedTask; });
            await CompleteAsync();
        }

        // Each update runs as dispatched work, the way the consumers run it, so a wait on the
        // clock inside it never blocks the scenario.
        private void Route(Update update)
        {
            var received = Clock.UtcNow;
            Bot.Dispatch(() =>
            {
                ErrorReporter.Current = ErrorContext.From(update);
                return UpdateHandler.RouteAsync(update, received);
            }, "HarnessRoute");
        }

        /// <summary>
        /// Waits until everything dispatched is done or waiting on the clock, firing timers that are
        /// already due, PostgreSQL writes included. Time does not move.
        /// </summary>
        internal Task SettleAsync(bool flush = true) => HarnessTime.SettleAsync(Clock, flush);

        /// <summary>
        /// Lets <paramref name="by"/> of simulated time pass: each timer fires at its due time, and
        /// what it sets off runs to completion (or to its next wait) before time moves on.
        /// </summary>
        internal Task AdvanceAsync(TimeSpan by) => HarnessTime.AdvanceAsync(Clock, by);

        /// <summary>
        /// Changes the group's detection settings as a menu would (in PostgreSQL, importing the seeded
        /// Redis hashes first if the group has none yet).
        /// </summary>
        internal Task<Enforcer5.Detection.DetectionPolicy> ChangeSettingsAsync(
            Func<Enforcer5.Detection.DetectionPolicy, Enforcer5.Detection.DetectionPolicy> change) =>
            Enforcer5.Detection.DetectionSettings.UpdateAsync(ChatId, change);

        /// <summary>The bot's messages to this chat, oldest first.</summary>
        internal IReadOnlyList<string> Replies =>
            Telegram.Accepted.Where(c => c.Method == "sendMessage" && c.ChatId == ChatId).Select(c => c.Text).ToList();

        public async ValueTask DisposeAsync()
        {
            if (!_owner) return;
            _world.QueueStop.Cancel();
            Enforcer5.Enforcement.EnforcementQueue.ResetForTests();
            // Let whatever still waits on this clock run out, so nothing is left holding a permit or
            // a static for the next scenario.
            try { await HarnessTime.CompleteAsync(Clock, TimeSpan.FromHours(2)); } catch (TimeoutException) { }
            ErrorReporter.Observer = _world.PreviousObserver;
            Bot.Api = _world.PreviousApi;
            Bot.Files = _world.PreviousFiles;
            Announcements.ResetForTests();
            Bot.Me = _world.PreviousMe;
            Enforcer5.Helpers.Clock.UseForTests(null);
            PostgresStorage.UseForTests(null);
            GlobalBans.ResetForTests();
            Enforcer5.Detection.DetectionSettings.ResetForTests();
            Enforcer5.Detection.DetectionEngine.ResetForTests();
            if (_world.Database != null) await _world.Database.DisposeAsync();
            if (_world.ConnectionString != null) await Storage.TestPostgres.ReleaseMigratedAsync(_world.ConnectionString);
        }
    }

    internal sealed record HarnessUser(long Id, string FirstName, string Username = null, string LastName = null,
        string LanguageCode = null, bool IsBot = false)
    {
        internal User ToUser() => new User { Id = Id, FirstName = FirstName, LastName = LastName, Username = Username,
            LanguageCode = LanguageCode, IsBot = IsBot };
    }
}
