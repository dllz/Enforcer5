using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    internal sealed class PostgresDetectionReportRepository : IDetectionReportRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresDetectionReportRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private static DateTime Utc(DateTimeOffset at) => at.UtcDateTime;

        private static DateTimeOffset Offset(DateTime at) => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));

        /// <summary>The group and the member, added if the principal flush has not written them yet.</summary>
        private static async Task<(long GroupId, long? AccountId)> EnsureAsync(DbConnection connection, DbTransaction transaction,
            long chatId, long? userId)
        {
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, chatId,
                chatType: PostgresBanHistoryRepository.ChatType(chatId));
            long? account = userId.HasValue
                ? (await PrincipalResolver.EnsureUserAsync(connection, transaction, userId.Value)).AccountId
                : null;
            return (group.GroupId, account);
        }

        public async Task AddCopiesAsync(ReportKey key, IReadOnlyList<ReportCopy> copies)
        {
            if (copies.Count == 0) return;
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var (groupId, accountId) = await EnsureAsync(connection, transaction, key.ChatId, key.UserId);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO report_copies (group_id, kind, account_id, filter_id, opened_at, chat_id, message_id, edition, sent_at)
SELECT @GroupId, @Kind, @AccountId, @FilterId, @OpenedAt, c.chat, c.message, @Edition, now()
FROM unnest(@Chats, @Messages) AS c(chat, message)",
                new
                {
                    GroupId = groupId, key.Kind, AccountId = accountId, key.FilterId, OpenedAt = Utc(key.OpenedAt),
                    Chats = copies.Select(c => c.ChatId).ToArray(), Messages = copies.Select(c => c.MessageId).ToArray(),
                    BanHistory.Edition
                }, transaction));
            await transaction.CommitAsync();
        }

        private const string KeyFilter = @"
JOIN groups g ON g.group_id = r.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
LEFT JOIN telegram_accounts a ON a.account_id = r.account_id
WHERE c.telegram_chat_id = @ChatId AND r.kind = @Kind AND r.edition = @Edition AND r.opened_at = @OpenedAt
  AND (@Kind <> 'member' OR a.telegram_user_id = @UserId)
  AND (@Kind <> 'trial' OR r.filter_id = @FilterId)";

        private sealed class CopyRow
        {
            public long ChatId { get; set; }
            public long MessageId { get; set; }
            public string Decision { get; set; }
            public long? DecidedBy { get; set; }
            public DateTime OpenedAt { get; set; }
        }

        private static object Args(ReportKey key) => new
        {
            key.ChatId, key.Kind, key.UserId, key.FilterId, OpenedAt = Utc(key.OpenedAt), BanHistory.Edition
        };

        private static StoredReport Stored(ReportKey key, IReadOnlyList<CopyRow> rows)
        {
            if (rows.Count == 0) return null;
            var decided = rows.FirstOrDefault(r => r.Decision != null);
            return new StoredReport(key, rows.Select(r => new ReportCopy(r.ChatId, r.MessageId)).ToList(), decided?.Decision, decided?.DecidedBy);
        }

        public async Task<StoredReport> GetAsync(ReportKey key)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = (await connection.QueryAsync<CopyRow>(new CommandDefinition(@"
SELECT r.chat_id AS ChatId, r.message_id AS MessageId, r.decision AS Decision, d.telegram_user_id AS DecidedBy, r.opened_at AS OpenedAt
FROM report_copies r
LEFT JOIN telegram_accounts d ON d.account_id = r.decided_by_account_id" + KeyFilter + @"
ORDER BY r.copy_id", Args(key)))).ToList();
            return Stored(key, rows);
        }

        public async Task<StoredReport> LatestMemberReportAsync(long chatId, long userId, DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var opened = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(@"
SELECT max(r.opened_at)
FROM report_copies r
JOIN groups g ON g.group_id = r.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = r.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId AND r.kind = 'member' AND r.edition = @Edition
  AND r.opened_at >= @Since",
                new { ChatId = chatId, UserId = userId, BanHistory.Edition, Since = Utc(since) }));
            if (opened == null) return null;
            return await GetAsync(ReportKey.ForMember(chatId, userId, Offset(opened.Value)));
        }

        public async Task<(string Decision, long? By)> DecideAsync(ReportKey key, string decision, long actorId, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var actor = (await PrincipalResolver.EnsureUserAsync(connection, transaction, actorId)).AccountId;
            // One decision per report: the copies are locked, and only undecided ones take it.
            var rows = (await connection.QueryAsync<CopyRow>(new CommandDefinition(@"
SELECT r.chat_id AS ChatId, r.message_id AS MessageId, r.decision AS Decision, d.telegram_user_id AS DecidedBy, r.opened_at AS OpenedAt
FROM report_copies r
LEFT JOIN telegram_accounts d ON d.account_id = r.decided_by_account_id" + KeyFilter + @"
ORDER BY r.copy_id
FOR UPDATE OF r", Args(key), transaction))).ToList();
            var earlier = rows.FirstOrDefault(r => r.Decision != null);
            if (earlier != null)
            {
                await transaction.CommitAsync();
                return (earlier.Decision, earlier.DecidedBy);
            }
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE report_copies r SET decision = @Decision, decided_by_account_id = @Actor, decided_at = @At
FROM groups g, telegram_chats c
WHERE g.group_id = r.group_id AND c.chat_id = g.chat_id AND c.telegram_chat_id = @ChatId
  AND r.kind = @Kind AND r.edition = @Edition AND r.opened_at = @OpenedAt AND r.decision IS NULL
  AND (@Kind <> 'member' OR r.account_id = (SELECT account_id FROM telegram_accounts WHERE telegram_user_id = @UserId))
  AND (@Kind <> 'trial' OR r.filter_id = @FilterId)",
                new
                {
                    key.ChatId, key.Kind, key.UserId, key.FilterId, OpenedAt = Utc(key.OpenedAt), BanHistory.Edition,
                    Decision = decision, Actor = actor, At = Utc(at)
                }, transaction));
            await transaction.CommitAsync();
            return (decision, actorId);
        }

        public async Task<IReadOnlyCollection<long>> OptedOutAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<long>(new CommandDefinition(@"
SELECT a.telegram_user_id
FROM report_admin_optouts o
JOIN groups g ON g.group_id = o.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = o.account_id
WHERE c.telegram_chat_id = @ChatId", new { ChatId = chatId }))).ToList();
        }

        public async Task SetOptedOutAsync(long chatId, long userId, bool optedOut)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var (groupId, accountId) = await EnsureAsync(connection, transaction, chatId, userId);
            await connection.ExecuteAsync(new CommandDefinition(optedOut
                    ? "INSERT INTO report_admin_optouts (group_id, account_id, opted_out_at) VALUES (@GroupId, @AccountId, now()) ON CONFLICT DO NOTHING"
                    : "DELETE FROM report_admin_optouts WHERE group_id = @GroupId AND account_id = @AccountId",
                new { GroupId = groupId, AccountId = accountId }, transaction));
            await transaction.CommitAsync();
        }

        public async Task<IReadOnlyList<DueTrial>> DueTrialsAsync(DateTimeOffset now, DateTimeOffset since, int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(long ChatId, long FilterId, DateTime TrialUntil)>(new CommandDefinition(@"
SELECT c.telegram_chat_id, f.filter_id, f.trial_until
FROM group_filters f
JOIN groups g ON g.group_id = f.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE f.mode = 'trial' AND f.enabled AND f.trial_notified_at IS NULL AND f.trial_until <= @Now AND f.trial_until >= @Since
ORDER BY f.trial_until
LIMIT @Limit", new { Now = Utc(now), Since = Utc(since), Limit = limit }));
            return rows.Select(r => new DueTrial(r.ChatId, r.FilterId, Offset(r.TrialUntil))).ToList();
        }

        public async Task<bool> ClaimTrialAsync(long filterId, DateTimeOffset trialUntil, DateTimeOffset now)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_filters SET trial_notified_at = @Now
WHERE filter_id = @FilterId AND mode = 'trial' AND trial_until = @TrialUntil AND trial_notified_at IS NULL",
                new { FilterId = filterId, TrialUntil = Utc(trialUntil), Now = Utc(now) })) == 1;
        }
    }
}
