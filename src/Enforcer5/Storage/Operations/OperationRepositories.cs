using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Enforcer5.Storage.Operations
{
    internal abstract class OperationRepository
    {
        protected OperationRepository(PostgresDatabase database) =>
            Database = database ?? throw new ArgumentNullException(nameof(database));
        protected PostgresDatabase Database { get; }
    }

    internal sealed class SanctionRepository : OperationRepository
    {
        internal SanctionRepository(PostgresDatabase database) : base(database) { }

        internal async Task<long> UpsertAsync(Sanction sanction, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions SET status = 'revoked', resolved_at = @CreatedAt
WHERE group_id = @GroupId AND user_id = @UserId AND kind = @Kind
  AND status IN ('active','due')", sanction, transaction, cancellationToken: cancellationToken));
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO sanctions (group_id, user_id, kind, status, edition, actor_user_id, reason,
    created_at, expires_at, claimed_at, claimed_by, resolved_at, legacy_key)
VALUES (@GroupId, @UserId, @Kind, @Status, @Edition, @ActorUserId, @Reason,
    @CreatedAt, @ExpiresAt, @ClaimedAt, @ClaimedBy, @ResolvedAt, @LegacyKey)
ON CONFLICT (legacy_key) WHERE legacy_key IS NOT NULL DO UPDATE SET
    group_id = EXCLUDED.group_id, user_id = EXCLUDED.user_id, kind = EXCLUDED.kind,
    status = EXCLUDED.status, edition = EXCLUDED.edition, actor_user_id = EXCLUDED.actor_user_id,
    reason = EXCLUDED.reason, created_at = EXCLUDED.created_at, expires_at = EXCLUDED.expires_at,
    resolved_at = EXCLUDED.resolved_at
RETURNING id", sanction, transaction, cancellationToken: cancellationToken));
            if (StorageModeProvider.RedisMirrorEnabled)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "sanctions",
                    $"sanction:{sanction.GroupId}:{sanction.UserId}", "set", sanction,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }

        internal async Task<IReadOnlyList<Sanction>> ClaimDueTempBansAsync(string edition,
            string workerId, int limit, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
            if (string.IsNullOrWhiteSpace(workerId)) throw new ArgumentException("Worker id is required.", nameof(workerId));
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var rows = await connection.QueryAsync<Sanction>(new CommandDefinition(@"
WITH due AS (
    SELECT id FROM sanctions
    WHERE kind = 'tempban' AND edition = @Edition
      AND (status = 'active' OR (status = 'due' AND claimed_at < @StaleBefore))
      AND expires_at <= @Now
    ORDER BY expires_at, id
    FOR UPDATE SKIP LOCKED
    LIMIT @Limit
)
UPDATE sanctions s SET status = 'due', claimed_at = @Now, claimed_by = @WorkerId
FROM due WHERE s.id = due.id
RETURNING s.id AS Id, s.group_id AS GroupId, s.user_id AS UserId, s.kind AS Kind,
    s.status AS Status, s.edition AS Edition, s.actor_user_id AS ActorUserId,
    s.reason AS Reason, s.created_at AS CreatedAt, s.expires_at AS ExpiresAt,
    s.claimed_at AS ClaimedAt, s.claimed_by AS ClaimedBy, s.resolved_at AS ResolvedAt,
    s.legacy_key AS LegacyKey", new { Edition = edition, WorkerId = workerId, Limit = limit,
                    Now = now, StaleBefore = now.Subtract(TimeSpan.FromHours(25)) },
                transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return rows.AsList();
        }

        internal async Task<bool> CompleteClaimAsync(long id, string workerId, DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var sanction = await connection.QuerySingleOrDefaultAsync<Sanction>(new CommandDefinition(@"
SELECT id AS Id, group_id AS GroupId, user_id AS UserId, kind AS Kind, status AS Status,
       edition AS Edition, actor_user_id AS ActorUserId, reason AS Reason, created_at AS CreatedAt,
       expires_at AS ExpiresAt, claimed_at AS ClaimedAt, claimed_by AS ClaimedBy,
       resolved_at AS ResolvedAt, legacy_key AS LegacyKey
FROM sanctions WHERE id = @Id AND status = 'due' AND claimed_by = @WorkerId FOR UPDATE",
                new { Id = id, WorkerId = workerId }, transaction, cancellationToken: cancellationToken));
            if (sanction == null) return false;
            var updated = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions SET status = 'completed', resolved_at = @CompletedAt,
    claimed_at = NULL, claimed_by = NULL
WHERE id = @Id AND status = 'due' AND claimed_by = @WorkerId",
                new { Id = id, WorkerId = workerId, CompletedAt = completedAt },
                transaction, cancellationToken: cancellationToken)) == 1;
            if (updated && StorageModeProvider.RedisMirrorEnabled)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "sanctions",
                    $"sanction:{sanction.GroupId}:{sanction.UserId}", "resolve",
                    new { groupId = sanction.GroupId, userId = sanction.UserId, kind = sanction.Kind },
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }

        internal async Task<bool> ReleaseClaimAsync(long id, string workerId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions SET status = 'active', claimed_at = NULL, claimed_by = NULL
WHERE id = @Id AND status = 'due' AND claimed_by = @WorkerId",
                new { Id = id, WorkerId = workerId }, cancellationToken: cancellationToken)) == 1;
        }

        internal async Task<Sanction> GetActiveAsync(long groupId, long userId, string kind,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<Sanction>(new CommandDefinition(@"
SELECT id AS Id, group_id AS GroupId, user_id AS UserId, kind AS Kind, status AS Status,
       edition AS Edition, actor_user_id AS ActorUserId, reason AS Reason, created_at AS CreatedAt,
       expires_at AS ExpiresAt, claimed_at AS ClaimedAt, claimed_by AS ClaimedBy,
       resolved_at AS ResolvedAt, legacy_key AS LegacyKey
FROM sanctions WHERE group_id = @GroupId AND user_id = @UserId AND kind = @Kind
  AND status IN ('active','due') ORDER BY created_at DESC LIMIT 1",
                new { GroupId = groupId, UserId = userId, Kind = kind }, cancellationToken: cancellationToken));
        }

        internal async Task<IReadOnlyList<Sanction>> ListActiveAsync(string kind = null,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<Sanction>(new CommandDefinition(@"
SELECT id AS Id, group_id AS GroupId, user_id AS UserId, kind AS Kind, status AS Status,
       edition AS Edition, actor_user_id AS ActorUserId, reason AS Reason, created_at AS CreatedAt,
       expires_at AS ExpiresAt, claimed_at AS ClaimedAt, claimed_by AS ClaimedBy,
       resolved_at AS ResolvedAt, legacy_key AS LegacyKey
FROM sanctions WHERE status IN ('active','due') AND (@Kind IS NULL OR kind = @Kind)
ORDER BY expires_at NULLS LAST, id", new { Kind = kind }, cancellationToken: cancellationToken));
            return rows.AsList();
        }

        internal async Task<IReadOnlyList<Sanction>> ListActiveForGroupAsync(long groupId,
            string kind = null, bool newJoinersOnly = false,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<Sanction>(new CommandDefinition(@"
SELECT id AS Id, group_id AS GroupId, user_id AS UserId, kind AS Kind, status AS Status,
       edition AS Edition, actor_user_id AS ActorUserId, reason AS Reason, created_at AS CreatedAt,
       expires_at AS ExpiresAt, claimed_at AS ClaimedAt, claimed_by AS ClaimedBy,
       resolved_at AS ResolvedAt, legacy_key AS LegacyKey
FROM sanctions WHERE group_id = @GroupId AND status IN ('active','due')
  AND (@Kind IS NULL OR kind = @Kind)
  AND (NOT @NewJoinersOnly OR legacy_key LIKE 'new-joiner:%') ORDER BY user_id",
                new { GroupId = groupId, Kind = kind, NewJoinersOnly = newJoinersOnly }, cancellationToken: cancellationToken));
            return rows.AsList();
        }
    }

    internal sealed class GlobalSanctionRepository : OperationRepository
    {
        internal GlobalSanctionRepository(PostgresDatabase database) : base(database) { }
        internal async Task UpsertAsync(GlobalSanction value, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO global_sanctions (user_id, active, reason, created_at, revocation_reason, revoked_at, legacy_key)
VALUES (@UserId, @Active, @Reason, @CreatedAt, @RevocationReason, @RevokedAt, @LegacyKey)
ON CONFLICT (user_id) DO UPDATE SET active = EXCLUDED.active, reason = EXCLUDED.reason,
    created_at = EXCLUDED.created_at, revocation_reason = EXCLUDED.revocation_reason,
    revoked_at = EXCLUDED.revoked_at, legacy_key = EXCLUDED.legacy_key", value,
                transaction, cancellationToken: cancellationToken));
            if (StorageModeProvider.RedisMirrorEnabled)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "global-sanctions",
                    $"global-sanction:{value.UserId}", "set", value, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal sealed class ReportRepository : OperationRepository
    {
        internal ReportRepository(PostgresDatabase database) : base(database) { }
        internal async Task<long> UpsertAsync(Report value, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO reports (group_id, report_message_id, reporter_user_id, reporter_display,
    reported_user_id, reported_message_id, status, created_at, solved_by_user_id,
    solved_by_display, solved_at, legacy_key)
VALUES (@GroupId, @ReportMessageId, @ReporterUserId, @ReporterDisplay, @ReportedUserId,
    @ReportedMessageId, @Status, @CreatedAt, @SolvedByUserId, @SolvedByDisplay, @SolvedAt, @LegacyKey)
ON CONFLICT (group_id, report_message_id) DO UPDATE SET reporter_user_id = EXCLUDED.reporter_user_id,
    reporter_display = EXCLUDED.reporter_display, reported_user_id = EXCLUDED.reported_user_id,
    reported_message_id = EXCLUDED.reported_message_id, status = EXCLUDED.status,
    created_at = EXCLUDED.created_at, solved_by_user_id = EXCLUDED.solved_by_user_id,
    solved_by_display = EXCLUDED.solved_by_display, solved_at = EXCLUDED.solved_at,
    legacy_key = EXCLUDED.legacy_key
RETURNING id", value, cancellationToken: cancellationToken));
        }

        internal async Task<bool> TrySolveAsync(long groupId, long reportMessageId, long solverUserId,
            string solverDisplay, DateTimeOffset solvedAt, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE reports SET status = 'solved', solved_by_user_id = @SolverUserId,
    solved_by_display = @SolverDisplay, solved_at = @SolvedAt
WHERE group_id = @GroupId AND report_message_id = @ReportMessageId AND status = 'open'",
                new { GroupId = groupId, ReportMessageId = reportMessageId, SolverUserId = solverUserId,
                    SolverDisplay = solverDisplay, SolvedAt = solvedAt },
                cancellationToken: cancellationToken)) == 1;
        }

        internal async Task UpsertDeliveryAsync(ReportDelivery value,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO report_deliveries (report_id, delivery_index, admin_chat_id, admin_message_id,
    forwarded_message_id, legacy_payload)
VALUES (@ReportId, @DeliveryIndex, @AdminChatId, @AdminMessageId, @ForwardedMessageId, @LegacyPayload)
ON CONFLICT (report_id, delivery_index) DO UPDATE SET admin_chat_id = EXCLUDED.admin_chat_id,
    admin_message_id = EXCLUDED.admin_message_id, forwarded_message_id = EXCLUDED.forwarded_message_id,
    legacy_payload = EXCLUDED.legacy_payload", value, cancellationToken: cancellationToken));
        }
    }

    internal sealed class StatisticsRepository : OperationRepository
    {
        internal StatisticsRepository(PostgresDatabase database) : base(database) { }
        internal async Task<long> GetUserMessagesAsync(long userId, CancellationToken token = default)
        {
            await using var connection = await Database.OpenConnectionAsync(token);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COALESCE((SELECT messages FROM user_statistics WHERE user_id = @UserId), 0)",
                new { UserId = userId }, cancellationToken: token));
        }
        internal async Task<long> GetGroupUserMessagesAsync(long groupId, long userId,
            CancellationToken token = default)
        {
            await using var connection = await Database.OpenConnectionAsync(token);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
SELECT COALESCE((SELECT messages FROM group_user_statistics
WHERE group_id = @GroupId AND user_id = @UserId), 0)",
                new { GroupId = groupId, UserId = userId }, cancellationToken: token));
        }
        internal async Task IncrementMessageAsync(string edition, long userId, long? groupId,
            DateTimeOffset occurredAt, CancellationToken token = default)
        {
            occurredAt = occurredAt.ToUniversalTime();
            await using var connection = await Database.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO bot_statistic_baselines (edition, messages, users, bans, kicks, captured_at)
VALUES (@Edition, 1, 0, 0, 0, @OccurredAt)
ON CONFLICT (edition) DO UPDATE SET messages = bot_statistic_baselines.messages + 1,
    captured_at = EXCLUDED.captured_at;

INSERT INTO user_statistics (user_id, messages, captured_at)
SELECT @UserId, 1, @OccurredAt WHERE @GroupId IS NOT NULL
ON CONFLICT (user_id) DO UPDATE SET messages = user_statistics.messages + 1,
    captured_at = EXCLUDED.captured_at;

INSERT INTO group_user_statistics (group_id, user_id, messages, last_message_at, captured_at)
SELECT @GroupId, @UserId, 1, @OccurredAt, @OccurredAt WHERE @GroupId IS NOT NULL
ON CONFLICT (group_id, user_id) DO UPDATE SET messages = group_user_statistics.messages + 1,
    last_message_at = EXCLUDED.last_message_at, captured_at = EXCLUDED.captured_at", new
            {
                Edition = edition,
                UserId = userId,
                GroupId = groupId,
                OccurredAt = occurredAt
            }, transaction, cancellationToken: token));
            if (StorageModeProvider.RedisMirrorEnabled)
            {
                var state = await connection.QuerySingleAsync<dynamic>(new CommandDefinition(@"
SELECT (SELECT COALESCE(SUM(messages), 0) FROM bot_statistic_baselines) AS BotMessages,
       COALESCE(u.messages, 0) AS UserMessages,
       COALESCE(g.messages, 0) AS GroupMessages
FROM bot_statistic_baselines b
LEFT JOIN user_statistics u ON u.user_id = @UserId
LEFT JOIN group_user_statistics g ON g.group_id = @GroupId AND g.user_id = @UserId
WHERE b.edition = @Edition", new { Edition = edition, UserId = userId, GroupId = groupId },
                    transaction, cancellationToken: token));
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "statistics",
                    "statistics:global", "set", new
                    {
                        edition, userId, groupId, occurredAt,
                        botMessages = (long)state.botmessages,
                        userMessages = (long)state.usermessages,
                        groupMessages = (long)state.groupmessages
                    }, token);
            }
            await transaction.CommitAsync(token);
        }

        internal Task UpsertAsync(UserStatistic value, CancellationToken token = default) => ExecuteAsync(@"
INSERT INTO user_statistics (user_id, messages, captured_at) VALUES (@UserId, @Messages, @CapturedAt)
ON CONFLICT (user_id) DO UPDATE SET messages = EXCLUDED.messages, captured_at = EXCLUDED.captured_at", value, token);
        internal Task UpsertAsync(GroupUserStatistic value, CancellationToken token = default) => ExecuteAsync(@"
INSERT INTO group_user_statistics (group_id, user_id, messages, last_message_at, captured_at)
VALUES (@GroupId, @UserId, @Messages, @LastMessageAt, @CapturedAt)
ON CONFLICT (group_id, user_id) DO UPDATE SET messages = EXCLUDED.messages,
    last_message_at = EXCLUDED.last_message_at, captured_at = EXCLUDED.captured_at", value, token);
        internal Task UpsertAsync(BotStatisticBaseline value, CancellationToken token = default) => ExecuteAsync(@"
INSERT INTO bot_statistic_baselines (edition, messages, users, bans, kicks, captured_at)
VALUES (@Edition, @Messages, @Users, @Bans, @Kicks, @CapturedAt)
ON CONFLICT (edition) DO UPDATE SET messages = EXCLUDED.messages, users = EXCLUDED.users,
    bans = EXCLUDED.bans, kicks = EXCLUDED.kicks, captured_at = EXCLUDED.captured_at", value, token);

        private async Task ExecuteAsync(string sql, object value, CancellationToken token)
        {
            await using var connection = await Database.OpenConnectionAsync(token);
            await connection.ExecuteAsync(new CommandDefinition(sql, value, cancellationToken: token));
        }
    }

    internal sealed class AuditEventRepository : OperationRepository
    {
        internal AuditEventRepository(PostgresDatabase database) : base(database) { }
        internal async Task<long> AppendAsync(AuditEvent value, CancellationToken token = default)
        {
            await using var connection = await Database.OpenConnectionAsync(token);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO audit_events (edition, event_type, group_id, actor_user_id, subject_user_id,
    details, occurred_at, legacy_key)
VALUES (@Edition, @EventType, @GroupId, @ActorUserId, @SubjectUserId, @Details, @OccurredAt, @LegacyKey)
ON CONFLICT (legacy_key) WHERE legacy_key IS NOT NULL DO UPDATE SET legacy_key = EXCLUDED.legacy_key
RETURNING id", value, cancellationToken: token));
        }
    }
}
