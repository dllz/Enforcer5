using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Enforcer5.Storage.Operations
{
    internal static class ReportStore
    {
        internal static ReportStoreRoute GetRoute(StorageMode mode, bool shadowEnabled) => mode switch
        {
            StorageMode.RedisPrimaryPostgresShadow => new ReportStoreRoute(true, shadowEnabled, true),
            StorageMode.PostgresPrimaryRedisMirror => new ReportStoreRoute(false, true, true),
            StorageMode.PostgresOnly => new ReportStoreRoute(false, true, false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        internal static async Task<ReportCreateResult> TryCreateAsync(ReportCreate request,
            CancellationToken cancellationToken = default)
        {
            var route = CurrentRoute(request.GroupId);
            if (route.ReadRedis)
            {
                var created = await TryCreateRedisAsync(request);
                var report = await ReadRedisAsync(request.GroupId, request.ReportMessageId);
                if (created && route.WritePostgres)
                {
                    await using var database = PostgresDatabase.FromConfiguration();
                    var id = await new ReportRepository(database).UpsertAsync(ToReport(request), cancellationToken);
                    report = report with { Id = id };
                }
                return new ReportCreateResult(created, report);
            }

            await using (var database = PostgresDatabase.FromConfiguration())
            {
                var result = await TryCreatePostgresAsync(database, request, cancellationToken);
                return result;
            }
        }

        internal static async Task<ReportRecord> GetAsync(long groupId, long reportMessageId,
            CancellationToken cancellationToken = default)
        {
            if (CurrentRoute(groupId).ReadRedis) return await ReadRedisAsync(groupId, reportMessageId);
            await using var database = PostgresDatabase.FromConfiguration();
            return await ReadPostgresAsync(database, groupId, reportMessageId, cancellationToken);
        }

        internal static async Task<ReportSolveResult> TrySolveAsync(long groupId, long reportMessageId,
            long solverUserId, string solverDisplay, DateTimeOffset solvedAt,
            CancellationToken cancellationToken = default)
        {
            var route = CurrentRoute(groupId);
            if (route.ReadRedis)
            {
                var outcome = await TrySolveRedisAsync(groupId, reportMessageId, solverUserId,
                    solverDisplay, solvedAt);
                var report = await ReadRedisAsync(groupId, reportMessageId);
                if (outcome == ReportSolveOutcome.Solved && route.WritePostgres)
                {
                    await using var database = PostgresDatabase.FromConfiguration();
                    await new ReportRepository(database).TrySolveAsync(groupId, reportMessageId,
                        solverUserId, solverDisplay, solvedAt, cancellationToken);
                }
                return new ReportSolveResult(outcome, report);
            }

            await using (var database = PostgresDatabase.FromConfiguration())
            {
                var solved = await TrySolvePostgresAsync(database, groupId, reportMessageId, solverUserId,
                    solverDisplay, solvedAt, route.WriteRedis, cancellationToken);
                var report = await ReadPostgresAsync(database, groupId, reportMessageId, cancellationToken);
                var outcome = solved ? ReportSolveOutcome.Solved : report == null
                    ? ReportSolveOutcome.NotFound : ReportSolveOutcome.AlreadySolved;
                return new ReportSolveResult(outcome, report);
            }
        }

        internal static async Task RecordDeliveryAsync(ReportRecord report, int index,
            long adminChatId, int adminMessageId, long sourceChatId, int sourceMessageId,
            CancellationToken cancellationToken = default)
        {
            var delivery = new ReportDeliveryRecord(index, adminChatId, adminMessageId,
                sourceChatId, sourceMessageId, Bot.Me.Id, OperationsStore.CurrentEdition);
            await Redis.db.HashSetAsync(RedisKey(report.GroupId, report.ReportMessageId),
                $"messageObject{index}", JsonConvert.SerializeObject(delivery));
        }

        internal static async Task<IReadOnlyList<ReportDeliveryRecord>> ListDeliveriesAsync(
            ReportRecord report, CancellationToken cancellationToken = default)
        {
            var countValue = await Redis.db.HashGetAsync(RedisKey(report.GroupId, report.ReportMessageId), "#Admin");
            if (!int.TryParse(countValue.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                return Array.Empty<ReportDeliveryRecord>();
            var values = await Redis.db.HashGetAsync(RedisKey(report.GroupId, report.ReportMessageId),
                Enumerable.Range(0, count).Select(i => (RedisValue)$"messageObject{i}").ToArray());
            return values.Select((value, index) => ParseDelivery(value, index))
                .Where(IsCurrentExecutor).ToArray();
        }

        internal static async Task SetDeliveryCountAsync(ReportRecord report, int count)
        {
            await Redis.db.HashSetAsync(RedisKey(report.GroupId, report.ReportMessageId), "#Admin", count);
        }

        private static async Task<bool> TryCreateRedisAsync(ReportCreate request)
        {
            var key = RedisKey(request.GroupId, request.ReportMessageId);
            if (!await Redis.db.HashSetAsync(key, "Solved", 0, When.NotExists)) return false;
            await WriteRedisAsync(request);
            return true;
        }

        private static Task WriteRedisAsync(ReportCreate request) => Redis.db.HashSetAsync(
            RedisKey(request.GroupId, request.ReportMessageId), new[]
            {
                new HashEntry("Solved", 0),
                new HashEntry("Created", FormatDate(request.CreatedAt)),
                new HashEntry("#Admin", 0),
                new HashEntry("Reporter", request.ReporterDisplay ?? string.Empty),
                new HashEntry("ReporterUserId", request.ReporterUserId),
                new HashEntry("ReportedUserId", request.ReportedUserId ?? 0),
                new HashEntry("ReportedMessageId", request.ReportedMessageId ?? 0),
                new HashEntry("repID", request.ReportMessageId)
            });

        private static async Task<ReportCreateResult> TryCreatePostgresAsync(PostgresDatabase database,
            ReportCreate request, CancellationToken cancellationToken)
        {
            var existing = await ReadPostgresAsync(database, request.GroupId, request.ReportMessageId,
                cancellationToken);
            if (existing != null) return new ReportCreateResult(false, existing);
            var id = await new ReportRepository(database).UpsertAsync(ToReport(request), cancellationToken);
            var report = await ReadPostgresAsync(database, request.GroupId, request.ReportMessageId,
                cancellationToken);
            return new ReportCreateResult(id != 0, report);
        }

        private static async Task<bool> TrySolvePostgresAsync(PostgresDatabase database, long groupId,
            long reportMessageId, long solverUserId, string solverDisplay, DateTimeOffset solvedAt,
            bool mirrorRedis, CancellationToken cancellationToken)
        {
            var solved = await new ReportRepository(database).TrySolveAsync(groupId, reportMessageId,
                solverUserId, solverDisplay, solvedAt, cancellationToken);
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (solved && mirrorRedis)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "reports",
                    $"report:{groupId}:{reportMessageId}", "solve",
                    new { groupId, reportMessageId, solverUserId, solverDisplay, solvedAt }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return solved;
        }

        private static async Task<ReportRecord> ReadPostgresAsync(PostgresDatabase database,
            long groupId, long reportMessageId, CancellationToken cancellationToken)
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            return await ReadPostgresAsync(connection, groupId, reportMessageId, cancellationToken);
        }

        private static Task<ReportRecord> ReadPostgresAsync(System.Data.Common.DbConnection connection,
            long groupId, long reportMessageId, CancellationToken cancellationToken,
            System.Data.Common.DbTransaction transaction = null) =>
            connection.QuerySingleOrDefaultAsync<ReportRecord>(new CommandDefinition(@"
SELECT r.report_id AS Id, tc.telegram_chat_id AS GroupId, r.report_message_id AS ReportMessageId,
        reporter.telegram_user_id AS ReporterUserId, r.reporter_display AS ReporterDisplay,
       target.telegram_user_id AS ReportedUserId, r.target_message_id AS ReportedMessageId,
       CASE WHEN r.status = 'open' THEN 'open' ELSE 'solved' END AS Status,
       r.reported_at AS CreatedAt, solver.telegram_user_id AS SolvedByUserId,
        r.solver_display AS SolvedByDisplay, r.resolved_at AS SolvedAt
FROM reports r JOIN groups g ON g.group_id = r.group_id
JOIN telegram_chats tc ON tc.chat_id = g.chat_id
LEFT JOIN telegram_accounts reporter ON reporter.account_id = r.reporter_account_id
LEFT JOIN telegram_accounts target ON target.account_id = r.target_account_id
LEFT JOIN user_identity_links sil ON sil.user_id = r.solver_user_id AND sil.unlinked_at IS NULL
LEFT JOIN telegram_accounts solver ON solver.account_id = sil.account_id
WHERE tc.telegram_chat_id = @GroupId AND r.report_message_id = @ReportMessageId",
                new { GroupId = groupId, ReportMessageId = reportMessageId },
                transaction, cancellationToken: cancellationToken));

        private static async Task<ReportRecord> ReadRedisAsync(long groupId, long reportMessageId)
        {
            var values = (await Redis.db.HashGetAllAsync(RedisKey(groupId, reportMessageId)))
                .ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString());
            if (!values.TryGetValue("Solved", out var solved)) return null;
            return new ReportRecord(reportMessageId, groupId, reportMessageId,
                ParseNullableLong(values, "ReporterUserId"), Value(values, "Reporter"),
                ParseNullableLong(values, "ReportedUserId"), ParseNullableLong(values, "ReportedMessageId"),
                solved == "1" ? "solved" : "open", ParseDate(Value(values, "Created")),
                ParseNullableLong(values, "SolvedByUserId"), Value(values, "solvedBy"),
                ParseDate(Value(values, "SolvedAt")));
        }

        private static async Task<ReportSolveOutcome> TrySolveRedisAsync(long groupId,
            long reportMessageId, long solverUserId, string solverDisplay, DateTimeOffset solvedAt)
        {
            const string script = @"
local solved = redis.call('HGET', KEYS[1], 'Solved')
if not solved then return 0 end
if solved ~= '0' then return 1 end
redis.call('HSET', KEYS[1], 'Solved', '1', 'SolvedAt', ARGV[1], 'solvedBy', ARGV[2], 'SolvedByUserId', ARGV[3])
return 2";
            var result = (long)await Redis.db.ScriptEvaluateAsync(script,
                new RedisKey[] { RedisKey(groupId, reportMessageId) },
                new RedisValue[] { FormatDate(solvedAt), solverDisplay, solverUserId });
            return (ReportSolveOutcome)result;
        }

        private static Task SetRedisSolvedAsync(long groupId, long reportMessageId, long solverUserId,
            string solverDisplay, DateTimeOffset solvedAt) => Redis.db.HashSetAsync(
            RedisKey(groupId, reportMessageId), new[]
            {
                new HashEntry("Solved", 1), new HashEntry("SolvedAt", FormatDate(solvedAt)),
                new HashEntry("solvedBy", solverDisplay), new HashEntry("SolvedByUserId", solverUserId)
            });

        private static ReportDeliveryRecord ParseDelivery(string payload, int fallbackIndex)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                var value = JsonConvert.DeserializeObject<ReportDeliveryRecord>(payload);
                if (value != null && value.AdminChatId != 0) return value;
                var legacy = JsonConvert.DeserializeObject<LegacyDelivery>(payload);
                return legacy?.adminChatId == 0 ? null : new ReportDeliveryRecord(fallbackIndex,
                    legacy.adminChatId, legacy.adminMsgId, legacy.chatId, legacy.chatMsgId, null, null);
            }
            catch (JsonException) { return null; }
        }

        private static Report ToReport(ReportCreate value) => new(0, value.GroupId,
            value.ReportMessageId, value.ReporterUserId, value.ReporterDisplay, value.ReportedUserId,
            value.ReportedMessageId, "open", value.CreatedAt, null, null, null, null);
        private static bool IsCurrentExecutor(ReportDeliveryRecord value) => value != null &&
            (value.ExecutorBotId.HasValue
                ? value.ExecutorBotId.Value == Bot.Me.Id
                : value.Executor == null || value.Executor == OperationsStore.CurrentEdition);
        private static ReportStoreRoute CurrentRoute(long groupId) => GetRoute(StorageRouting.EffectiveGroupMode(groupId),
            string.Equals(RegHelper.GetRegValue("OperationsShadowEnabled"), "true",
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection")));
        private static string RedisKey(long groupId, long reportMessageId) =>
            $"flagged:{groupId}:{reportMessageId}";
        private static string FormatDate(DateTimeOffset value) =>
            value.UtcDateTime.ToString("hh:mm:ss dd-MM-yyyy", CultureInfo.InvariantCulture);
        private static DateTimeOffset? ParseDate(string value) =>
            DateTime.TryParseExact(value, "hh:mm:ss dd-MM-yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? new DateTimeOffset(parsed, TimeSpan.Zero) : null;
        private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) ? value : null;
        private static long? ParseNullableLong(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) && parsed != 0
                ? parsed : null;

        private sealed class LegacyDelivery
        {
            public int chatMsgId { get; set; }
            public long chatId { get; set; }
            public long adminChatId { get; set; }
            public int adminMsgId { get; set; }
        }
    }

    internal readonly record struct ReportStoreRoute(bool ReadRedis, bool WritePostgres, bool WriteRedis);
    internal sealed record ReportCreate(long GroupId, long ReportMessageId, long ReporterUserId,
        string ReporterDisplay, long? ReportedUserId, long? ReportedMessageId, DateTimeOffset CreatedAt);
    internal sealed record ReportRecord(long Id, long GroupId, long ReportMessageId,
        long? ReporterUserId, string ReporterDisplay, long? ReportedUserId, long? ReportedMessageId,
        string Status, DateTimeOffset? CreatedAt, long? SolvedByUserId, string SolvedByDisplay,
        DateTimeOffset? SolvedAt);
    internal sealed record ReportDeliveryRecord(int Index, long AdminChatId, int AdminMessageId,
        long SourceChatId, int SourceMessageId, long? ExecutorBotId, string Executor);
    internal sealed record ReportCreateResult(bool Created, ReportRecord Report);
    internal sealed record ReportSolveResult(ReportSolveOutcome Outcome, ReportRecord Report);
    internal enum ReportSolveOutcome { NotFound = 0, AlreadySolved = 1, Solved = 2 }
}
