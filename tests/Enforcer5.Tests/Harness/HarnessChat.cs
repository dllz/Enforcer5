using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Handlers;
using Enforcer5.Helpers;
using Enforcer5.Storage;
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
    /// Built for the PostgreSQL rollout and grown with it, one domain at a time. Redis is a loose
    /// stand-in that answers every read with "nothing stored" and drops writes, so scenarios cover
    /// what PostgreSQL holds, not Redis state. PostgreSQL is a throwaway schema on the test
    /// database (ENFORCER_TEST_POSTGRES); without one the scenarios are skipped.
    /// </para>
    /// <para>
    /// The bot is static throughout, so a harness takes over Bot.Api, Redis.db and PostgresStorage
    /// for its lifetime and gives them back on Dispose. Scenarios must not run in parallel.
    /// </para>
    /// </summary>
    internal sealed class HarnessChat : IAsyncDisposable
    {
        internal static readonly User BotUser = new User { Id = 777000111, IsBot = true, FirstName = "Enforcer", Username = "EnforcerHarnessBot" };

        private readonly ConcurrentQueue<string> _errors = new();
        private readonly Action<string> _previousObserver;
        private readonly Telegram.Bot.ITelegramBotClient _previousApi;
        private readonly User _previousMe;
        private string _connectionString;
        private PostgresDatabase _database;
        private int _nextUpdateId = 1;

        internal long ChatId { get; }
        internal string Title { get; set; }
        /// <summary>The group's public username, null for a private group.</summary>
        internal string Username { get; set; }
        internal FakeTelegram Telegram { get; }
        internal Mock<IDatabase> Redis { get; } = new Mock<IDatabase>();

        /// <summary>Everything ErrorReporter saw while the harness ran.</summary>
        internal IReadOnlyList<string> Errors => _errors.ToList();

        /// <summary>The database the scenario's PostgreSQL writes went to.</summary>
        internal PostgresDatabase Database => _database;

        private HarnessChat(long chatId, string title)
        {
            ChatId = chatId;
            Title = title;
            Telegram = new FakeTelegram(BotUser);
            _previousApi = Bot.Api;
            _previousMe = Bot.Me;
            _previousObserver = ErrorReporter.Observer;
        }

        /// <summary>A new supergroup with a fresh PostgreSQL schema. Skips the test without a database.</summary>
        internal static async Task<HarnessChat> StartAsync(long chatId = -1001234567890, string title = "Harness group",
            string username = null)
        {
            var chat = new HarnessChat(chatId, title);
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

        private async Task SetUpAsync()
        {
            _connectionString = await Storage.TestPostgres.CreateSchemaAsync();
            await SchemaMigrationRunner.ApplyAsync(_connectionString);
            _database = new PostgresDatabase(_connectionString);
            PostgresStorage.UseForTests(_database);

#if PREMIUM
            // The premium edition only serves groups on its list.
            Redis.Setup(r => r.SetContainsAsync(It.Is<RedisKey>(k => k == "premiumBot"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
#endif
            Enforcer5.Helpers.Redis.UseForTests(Redis.Object);
            Bot.Api = Telegram;
            Bot.Me = BotUser;
            Bot.StartTime = DateTime.UtcNow.AddMinutes(-1);
            Bot.BindHandlers();
            if (!Program.LangaugeList.Any())
                Methods.IntialiseLanguages();
            ErrorReporter.Observer = report => _errors.Enqueue(report);
            Telegram.SetMember(ChatId, BotUser.Id, MemberState.Administrator);
        }

        /// <summary>Adds someone to the chat as Telegram would know them.</summary>
        internal HarnessUser Add(string firstName, MemberState state = MemberState.Member, string username = null, long? id = null,
            string lastName = null, string languageCode = null)
        {
            var user = new HarnessUser(id ?? 100000 + Telegram.NextMessageId(), firstName, username, lastName, languageCode);
            Telegram.SetMember(ChatId, user.Id, state);
            return user;
        }

        /// <summary>
        /// Posts <paramref name="text"/> as <paramref name="from"/> and waits until everything it set
        /// off is done, PostgreSQL writes included. Returns the message, so others can reply to it.
        /// </summary>
        internal async Task<Message> SendAsync(HarnessUser from, string text, Message replyTo = null)
        {
            var message = new Message
            {
                Id = Telegram.NextMessageId(),
                Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup, Title = Title, Username = Username },
                From = from.ToUser(),
                Date = DateTime.UtcNow,
                Text = text,
                ReplyToMessage = replyTo,
                Entities = text.StartsWith("/")
                    ? new[] { new MessageEntity { Type = MessageEntityType.BotCommand, Offset = 0, Length = text.Split(' ')[0].Length } }
                    : null
            };
            await UpdateHandler.RouteAsync(new Update { Id = _nextUpdateId++, Message = message }, DateTime.Now);
            await Bot.WaitIdleAsync(TimeSpan.FromSeconds(30));
            await BanHistory.FlushAsync();
            await PrincipalObserver.FlushAsync();
            return message;
        }

        /// <summary>The bot's messages to this chat, oldest first.</summary>
        internal IReadOnlyList<string> Replies =>
            Telegram.Calls.Where(c => c.Method == "sendMessage" && c.ChatId == ChatId).Select(c => c.Text).ToList();

        public async ValueTask DisposeAsync()
        {
            ErrorReporter.Observer = _previousObserver;
            Bot.Api = _previousApi;
            Bot.Me = _previousMe;
            PostgresStorage.UseForTests(null);
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await Storage.TestPostgres.DropSchemaAsync(_connectionString);
        }
    }

    internal sealed record HarnessUser(long Id, string FirstName, string Username = null, string LastName = null,
        string LanguageCode = null)
    {
        internal User ToUser() => new User { Id = Id, FirstName = FirstName, LastName = LastName, Username = Username,
            LanguageCode = LanguageCode };
    }
}
