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
            // Legacy temp sanctions do not retain their creation instant. If already expired at
            // capture time, use expiry as the earliest defensible imposition timestamp.
            var imposedAt = sanction.ExpiresAt.HasValue && sanction.ExpiresAt.Value < sanction.CreatedAt
                ? sanction.ExpiresAt.Value
                : sanction.CreatedAt;
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = await OperationsPrincipalResolver.ResolveGroupAsync(connection, transaction,
                sanction.GroupId, imposedAt, cancellationToken);
            var subject = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                sanction.UserId, imposedAt, cancellationToken);
            long? actorUserId = null;
            if (sanction.ActorUserId.HasValue)
                actorUserId = (await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                    sanction.ActorUserId.Value, imposedAt, cancellationToken)).UserId;
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions SET status = 'revoked', resolved_at = @ImposedAt, claimed_at = NULL, claimed_by = NULL
WHERE group_id = @GroupId AND account_id = @AccountId AND kind = @Kind
  AND status IN ('active','due') AND (@LegacyKey IS NULL OR legacy_key <> @LegacyKey)",
                new { GroupId = groupId, subject.AccountId, sanction.Kind, ImposedAt = imposedAt,
                    sanction.LegacyKey }, transaction, cancellationToken: cancellationToken));
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO sanctions (group_id, account_id, user_id_at_time, actor_user_id, kind, status, reason,
    imposed_at, expires_at, resolved_at, edition, claimed_at, claimed_by, legacy_key)
VALUES (@GroupId, @AccountId, @UserIdAtTime, @ActorUserId, @Kind, @Status, @Reason,
    @ImposedAt, @ExpiresAt, @ResolvedAt, @Edition, @ClaimedAt, @ClaimedBy, @LegacyKey)
ON CONFLICT (legacy_key) WHERE legacy_key IS NOT NULL DO UPDATE SET
    group_id = EXCLUDED.group_id, account_id = EXCLUDED.account_id,
    user_id_at_time = EXCLUDED.user_id_at_time, actor_user_id = EXCLUDED.actor_user_id,
    kind = EXCLUDED.kind, status = EXCLUDED.status, reason = EXCLUDED.reason,
    imposed_at = EXCLUDED.imposed_at, expires_at = EXCLUDED.expires_at,
    resolved_at = EXCLUDED.resolved_at, edition = EXCLUDED.edition
RETURNING sanction_id", new { GroupId = groupId, subject.AccountId,
                    UserIdAtTime = subject.UserId, ActorUserId = actorUserId, sanction.Kind, sanction.Status,
                    sanction.Reason, ImposedAt = imposedAt, sanction.ExpiresAt, sanction.ResolvedAt,
                    sanction.Edition, sanction.ClaimedAt, sanction.ClaimedBy, sanction.LegacyKey }, transaction,
                cancellationToken: cancellationToken));
            if (StorageRouting.GroupRedisMirrorEnabled(sanction.GroupId))
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "sanctions",
                    $"sanction:{sanction.GroupId}:{sanction.UserId}", "set", sanction, cancellationToken);
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
    SELECT s.sanction_id FROM sanctions s
    WHERE s.kind = 'tempban' AND s.edition = @Edition
      AND (s.status = 'active' OR (s.status = 'due' AND s.claimed_at < @StaleBefore))
      AND s.expires_at <= @Now
      -- Only groups PostgreSQL owns. The Redis expiry worker skips those same groups, so during a
      -- mixed population the two workers partition the estate rather than both acting on a group
      -- or, worse, neither. Once the process-wide mode is flipped it is the ceiling for every
      -- group, and there are no per-group rows to match, so @AllGroups short-circuits the check.
      AND (@AllGroups OR EXISTS (
          SELECT 1
          FROM groups g
          JOIN telegram_chats tc ON tc.chat_id = g.chat_id
          JOIN migration_group_state m ON m.telegram_chat_id = tc.telegram_chat_id
          WHERE g.group_id = s.group_id AND m.state = 'Postgres'))
    ORDER BY s.expires_at, s.sanction_id FOR UPDATE SKIP LOCKED LIMIT @Limit
)
UPDATE sanctions s SET status = 'due', claimed_at = @Now, claimed_by = @WorkerId
FROM due WHERE s.sanction_id = due.sanction_id
RETURNING s.sanction_id AS Id,
    (SELECT telegram_chat_id FROM telegram_chats tc JOIN groups g ON g.chat_id = tc.chat_id WHERE g.group_id = s.group_id) AS GroupId,
    (SELECT telegram_user_id FROM telegram_accounts ta WHERE ta.account_id = s.account_id) AS UserId,
    s.kind AS Kind, s.status AS Status, s.edition AS Edition,
    (SELECT telegram_user_id FROM user_identity_links l JOIN telegram_accounts ta ON ta.account_id = l.account_id WHERE l.user_id = s.actor_user_id AND l.unlinked_at IS NULL LIMIT 1) AS ActorUserId,
    s.reason AS Reason, s.imposed_at AS CreatedAt, s.expires_at AS ExpiresAt,
    s.claimed_at AS ClaimedAt, s.claimed_by AS ClaimedBy, s.resolved_at AS ResolvedAt,
    s.legacy_key AS LegacyKey", new { Edition = edition, WorkerId = workerId, Limit = limit,
                    Now = now, StaleBefore = now.Subtract(TimeSpan.FromHours(25)),
                    AllGroups = StorageModeProvider.PostgresIsPrimary }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return rows.AsList();
        }

        internal async Task<bool> CompleteClaimAsync(long id, string workerId, DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Fetched only to build the mirror payload. The sanction's own group decides whether
            // it is mirrored, but that is not known until it has been read, so read it whenever any
            // mirroring is possible and filter below.
            var sanction = await QueryByClaimAsync(connection, transaction, id, workerId, cancellationToken);
            var updated = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions SET status = 'completed', resolved_at = @CompletedAt, claimed_at = NULL, claimed_by = NULL
WHERE sanction_id = @Id AND status = 'due' AND claimed_by = @WorkerId",
                new { Id = id, WorkerId = workerId, CompletedAt = completedAt }, transaction,
                cancellationToken: cancellationToken)) == 1;
            if (updated && sanction != null && StorageRouting.GroupRedisMirrorEnabled(sanction.GroupId))
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "sanctions",
                    $"sanction:{sanction.GroupId}:{sanction.UserId}", "resolve",
                    new { groupId = sanction.GroupId, userId = sanction.UserId, kind = sanction.Kind }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }

        internal async Task<bool> ReleaseClaimAsync(long id, string workerId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions SET status = 'active', claimed_at = NULL, claimed_by = NULL
WHERE sanction_id = @Id AND status = 'due' AND claimed_by = @WorkerId",
                new { Id = id, WorkerId = workerId }, cancellationToken: cancellationToken)) == 1;
        }

        internal async Task<Sanction> GetActiveAsync(long groupId, long userId, string kind,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<Sanction>(new CommandDefinition(
                SelectSql + @" WHERE tc.telegram_chat_id = @GroupId AND ta.telegram_user_id = @UserId
AND s.kind = @Kind AND s.status IN ('active','due') ORDER BY s.imposed_at DESC LIMIT 1",
                new { GroupId = groupId, UserId = userId, Kind = kind }, cancellationToken: cancellationToken));
        }

        internal async Task<IReadOnlyList<Sanction>> ListActiveAsync(string kind = null,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<Sanction>(new CommandDefinition(
                SelectSql + @" WHERE s.status IN ('active','due') AND (@Kind IS NULL OR s.kind = @Kind)
ORDER BY s.expires_at NULLS LAST, s.sanction_id", new { Kind = kind }, cancellationToken: cancellationToken));
            return rows.AsList();
        }

        internal async Task<IReadOnlyList<Sanction>> ListActiveForGroupAsync(long groupId,
            string kind = null, bool newJoinersOnly = false, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<Sanction>(new CommandDefinition(
                SelectSql + @" WHERE tc.telegram_chat_id = @GroupId AND s.status IN ('active','due')
AND (@Kind IS NULL OR s.kind = @Kind) AND (NOT @NewJoinersOnly OR s.legacy_key LIKE 'new-joiner:%')
ORDER BY ta.telegram_user_id", new { GroupId = groupId, Kind = kind, NewJoinersOnly = newJoinersOnly },
                cancellationToken: cancellationToken));
            return rows.AsList();
        }

        private static Task<Sanction> QueryByClaimAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, long id, string workerId, CancellationToken token) =>
            connection.QuerySingleOrDefaultAsync<Sanction>(new CommandDefinition(
                // "FOR UPDATE OF s", not a bare "FOR UPDATE": SelectSql LEFT JOINs the actor's
                // account, and PostgreSQL rejects locking the nullable side of an outer join with
                // "FOR UPDATE cannot be applied to the nullable side of an outer join". Only the
                // sanction row needs locking anyway. Unqualified, this threw on every call, and the
                // caller treated the failure as an unban that could not be completed - so the claim
                // was released instead, and the tempban never expired.
                SelectSql + " WHERE s.sanction_id = @Id AND s.status = 'due' AND s.claimed_by = @WorkerId FOR UPDATE OF s",
                new { Id = id, WorkerId = workerId }, transaction, cancellationToken: token));

        private const string SelectSql = @"
SELECT s.sanction_id AS Id, tc.telegram_chat_id AS GroupId, ta.telegram_user_id AS UserId,
       s.kind AS Kind, s.status AS Status, s.edition AS Edition, actor.telegram_user_id AS ActorUserId,
       s.reason AS Reason, s.imposed_at AS CreatedAt, s.expires_at AS ExpiresAt,
       s.claimed_at AS ClaimedAt, s.claimed_by AS ClaimedBy, s.resolved_at AS ResolvedAt,
       s.legacy_key AS LegacyKey
FROM sanctions s JOIN groups g ON g.group_id = s.group_id
JOIN telegram_chats tc ON tc.chat_id = g.chat_id
JOIN telegram_accounts ta ON ta.account_id = s.account_id
LEFT JOIN LATERAL (SELECT account_id FROM user_identity_links
    WHERE user_id = s.actor_user_id AND unlinked_at IS NULL ORDER BY linked_at DESC LIMIT 1) ail ON TRUE
LEFT JOIN telegram_accounts actor ON actor.account_id = ail.account_id";
    }

    internal sealed class GlobalSanctionRepository : OperationRepository
    {
        internal GlobalSanctionRepository(PostgresDatabase database) : base(database) { }
        internal async Task UpsertAsync(GlobalSanction value, CancellationToken cancellationToken = default)
        {
            var occurredAt = value.CreatedAt ?? value.RevokedAt ?? DateTimeOffset.UtcNow;
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var principal = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                value.UserId, occurredAt, cancellationToken);
            if (value.Active)
            {
                var updated = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE global_sanctions SET status = 'active', reason = @Reason, imposed_at = @ImposedAt,
    resolved_at = NULL, legacy_key = COALESCE(@LegacyKey, legacy_key)
WHERE account_id = @AccountId AND status = 'active'", new { principal.AccountId,
                        value.Reason, ImposedAt = occurredAt, value.LegacyKey }, transaction,
                    cancellationToken: cancellationToken));
                if (updated == 0)
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO global_sanctions (account_id, user_id_at_time, kind, status, reason, imposed_at, legacy_key)
VALUES (@AccountId, @UserId, 'ban', 'active', @Reason, @ImposedAt, @LegacyKey)
ON CONFLICT (legacy_key) WHERE legacy_key IS NOT NULL DO UPDATE SET status = 'active', reason = EXCLUDED.reason,
    imposed_at = EXCLUDED.imposed_at, resolved_at = NULL", new { principal.AccountId,
                        UserId = principal.UserId, value.Reason, ImposedAt = occurredAt, value.LegacyKey },
                    transaction, cancellationToken: cancellationToken));
            }
            else
            {
                // A revoked global ban is still durable moderation history: why the user was banned,
                // when, why it was lifted and when. Previously this was an UPDATE only, so a record
                // with no pre-existing active row produced nothing at all - no row, no exception, no
                // trace. That accounted for exactly the 83 of 1,843 globalBan keys that never
                // arrived, and it is the kind of silence the rollout's first property forbids.
                //
                // resolved_at is clamped to imposed_at because global_sanctions_period_check
                // requires resolved_at >= imposed_at and the legacy timestamps are not guaranteed
                // to be ordered - two different formats written by two different hosts.
                var resolvedAt = value.RevokedAt ?? occurredAt;
                if (resolvedAt < occurredAt) resolvedAt = occurredAt;
                var revoked = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE global_sanctions SET status = 'revoked', resolution_reason = COALESCE(@ResolutionReason, resolution_reason),
    resolved_at = GREATEST(@ResolvedAt, imposed_at)
WHERE account_id = @AccountId AND status = 'active'", new { principal.AccountId,
                        ResolutionReason = value.RevocationReason, ResolvedAt = resolvedAt }, transaction,
                    cancellationToken: cancellationToken));
                if (revoked == 0)
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO global_sanctions (account_id, user_id_at_time, kind, status, reason, resolution_reason,
    imposed_at, resolved_at, legacy_key)
VALUES (@AccountId, @UserId, 'ban', 'revoked', @Reason, @ResolutionReason, @ImposedAt, @ResolvedAt, @LegacyKey)
ON CONFLICT (legacy_key) WHERE legacy_key IS NOT NULL DO UPDATE SET status = 'revoked',
    reason = EXCLUDED.reason, resolution_reason = EXCLUDED.resolution_reason,
    resolved_at = EXCLUDED.resolved_at", new { principal.AccountId, UserId = principal.UserId,
                            value.Reason, ResolutionReason = value.RevocationReason,
                            ImposedAt = occurredAt, ResolvedAt = resolvedAt, value.LegacyKey },
                        transaction, cancellationToken: cancellationToken));
            }
            // Global tier: no group dimension, so this follows the process-wide mirror flag.
            if (StorageRouting.RedisMirrorEnabled)
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
            var occurredAt = value.CreatedAt ?? DateTimeOffset.UtcNow;
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = await OperationsPrincipalResolver.ResolveGroupAsync(connection, transaction,
                value.GroupId, occurredAt, cancellationToken);
            OperationsUserPrincipal? reporter = value.ReporterUserId.HasValue
                ? await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                    value.ReporterUserId.Value, occurredAt, cancellationToken)
                : null;
            OperationsUserPrincipal? target = value.ReportedUserId.HasValue
                ? await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                    value.ReportedUserId.Value, occurredAt, cancellationToken)
                : null;
            long? solverUserId = null;
            if (value.SolvedByUserId.HasValue)
                solverUserId = (await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                    value.SolvedByUserId.Value, value.SolvedAt ?? occurredAt, cancellationToken)).UserId;
            await EnsureMessageAsync(connection, transaction, groupId, value.ReportMessageId,
                reporter, occurredAt, cancellationToken);
            if (value.ReportedMessageId.HasValue)
                await EnsureMessageAsync(connection, transaction, groupId, value.ReportedMessageId.Value,
                    target, occurredAt, cancellationToken);
            var resolved = value.Status != "open";
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtextextended(@LockKey, 0))",
                new { LockKey = $"report:{groupId}:{value.ReportMessageId}" }, transaction,
                cancellationToken: cancellationToken));
            var existingId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(@"
SELECT report_id FROM reports WHERE group_id = @GroupId AND report_message_id = @ReportMessageId",
                new { GroupId = groupId, value.ReportMessageId }, transaction,
                cancellationToken: cancellationToken));
            var parameters = new { GroupId = groupId, ReporterUserId = reporter?.UserId,
                ReporterAccountId = reporter?.AccountId, value.ReporterDisplay,
                TargetUserId = target?.UserId, TargetAccountId = target?.AccountId,
                SolverUserId = solverUserId, SolverDisplay = value.SolvedByDisplay,
                value.ReportMessageId, TargetMessageId = value.ReportedMessageId,
                Status = resolved ? "resolved" : "open", ResolutionAction = resolved ? "solved" : null,
                ReportedAt = occurredAt, ResolvedAt = resolved
                    ? (DateTimeOffset?)(value.SolvedAt ?? occurredAt) : null,
                value.LegacyKey };
            long id;
            if (existingId.HasValue)
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE reports SET reporter_user_id = @ReporterUserId, reporter_account_id = @ReporterAccountId,
    reporter_display = @ReporterDisplay,
    target_user_id = @TargetUserId, target_account_id = @TargetAccountId,
    solver_user_id = @SolverUserId, solver_display = @SolverDisplay,
    status = @Status, resolution_action = @ResolutionAction,
    reported_at = @ReportedAt, resolved_at = @ResolvedAt, legacy_key = COALESCE(@LegacyKey, legacy_key)
WHERE report_id = @ExistingId", new { ExistingId = existingId.Value, parameters.ReporterUserId,
                        parameters.ReporterAccountId, parameters.ReporterDisplay,
                        parameters.TargetUserId, parameters.TargetAccountId,
                        parameters.SolverUserId, parameters.SolverDisplay,
                        parameters.Status, parameters.ResolutionAction,
                        parameters.ReportedAt, parameters.ResolvedAt, parameters.LegacyKey }, transaction,
                    cancellationToken: cancellationToken));
                id = existingId.Value;
            }
            else id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO reports (group_id, reporter_user_id, reporter_account_id, reporter_display,
    target_user_id, target_account_id, solver_user_id, solver_display,
    report_message_id, target_message_id, status, resolution_action,
    reported_at, resolved_at, legacy_key)
VALUES (@GroupId, @ReporterUserId, @ReporterAccountId, @ReporterDisplay,
    @TargetUserId, @TargetAccountId, @SolverUserId, @SolverDisplay,
    @ReportMessageId, @TargetMessageId, @Status, @ResolutionAction,
    @ReportedAt, @ResolvedAt, @LegacyKey)
ON CONFLICT (legacy_key) WHERE legacy_key IS NOT NULL DO UPDATE SET
    reporter_user_id = EXCLUDED.reporter_user_id, reporter_account_id = EXCLUDED.reporter_account_id,
    target_user_id = EXCLUDED.target_user_id, target_account_id = EXCLUDED.target_account_id,
    solver_user_id = EXCLUDED.solver_user_id, status = EXCLUDED.status,
    resolution_action = EXCLUDED.resolution_action, resolved_at = EXCLUDED.resolved_at
RETURNING report_id", parameters, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return id;
        }

        internal async Task<bool> TrySolveAsync(long groupId, long reportMessageId, long solverUserId,
            string solverDisplay, DateTimeOffset solvedAt, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var internalGroupId = await OperationsPrincipalResolver.ResolveGroupAsync(connection, transaction,
                groupId, solvedAt, cancellationToken);
            var solver = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                solverUserId, solvedAt, cancellationToken);
            var changed = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE reports SET status = 'resolved', resolution_action = 'solved', solver_user_id = @SolverUserId,
    resolved_at = @SolvedAt WHERE group_id = @GroupId AND report_message_id = @ReportMessageId AND status = 'open'",
                new { GroupId = internalGroupId, ReportMessageId = reportMessageId,
                    SolverUserId = solver.UserId, SolvedAt = solvedAt }, transaction,
                cancellationToken: cancellationToken)) == 1;
            await transaction.CommitAsync(cancellationToken);
            return changed;
        }

        private static Task EnsureMessageAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, long groupId, long messageId,
            OperationsUserPrincipal? principal, DateTimeOffset sentAt, CancellationToken token) =>
            connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO message_metadata (group_id, telegram_message_id, account_id, user_id_at_time,
    message_type, has_media, sent_at)
VALUES (@GroupId, @MessageId, @AccountId, @UserId, 'unknown', FALSE, @SentAt)
ON CONFLICT (group_id, telegram_message_id) DO NOTHING", new { GroupId = groupId, MessageId = messageId,
                    AccountId = principal?.AccountId, UserId = principal?.UserId, SentAt = sentAt }, transaction,
                cancellationToken: token));
    }

    internal sealed class StatisticsRepository : OperationRepository
    {
        internal StatisticsRepository(PostgresDatabase database) : base(database) { }

        internal async Task<long> GetUserMessagesAsync(long userId, CancellationToken token = default)
        {
            await using var connection = await Database.OpenConnectionAsync(token);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
SELECT COALESCE((SELECT amt.message_count FROM account_message_totals amt
JOIN telegram_accounts ta ON ta.account_id = amt.account_id WHERE ta.telegram_user_id = @UserId), 0)",
                new { UserId = userId }, cancellationToken: token));
        }

        internal async Task<long> GetGroupUserMessagesAsync(long groupId, long userId,
            CancellationToken token = default)
        {
            await using var connection = await Database.OpenConnectionAsync(token);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
SELECT COALESCE((SELECT gamt.message_count FROM group_account_message_totals gamt
JOIN groups g ON g.group_id = gamt.group_id JOIN telegram_chats tc ON tc.chat_id = g.chat_id
JOIN telegram_accounts ta ON ta.account_id = gamt.account_id
WHERE tc.telegram_chat_id = @GroupId AND ta.telegram_user_id = @UserId), 0)",
                new { GroupId = groupId, UserId = userId }, cancellationToken: token));
        }

        internal async Task IncrementMessageAsync(string edition, long userId, long? groupId,
            DateTimeOffset occurredAt, CancellationToken token = default)
        {
            occurredAt = occurredAt.ToUniversalTime();
            await using var connection = await Database.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            var principal = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                userId, occurredAt, token);
            long? resolvedGroupId = null;
            if (groupId.HasValue)
                resolvedGroupId = await OperationsPrincipalResolver.ResolveGroupAsync(connection, transaction,
                    groupId.Value, occurredAt, token);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO bot_statistic_baselines (edition, message_count, user_count, ban_count, kick_count, captured_at)
VALUES (@Edition, 1, 0, 0, 0, @OccurredAt)
ON CONFLICT (edition) DO UPDATE SET message_count = bot_statistic_baselines.message_count + 1,
    captured_at = EXCLUDED.captured_at;
INSERT INTO account_message_totals (account_id, message_count, captured_at)
VALUES (@AccountId, 1, @OccurredAt)
ON CONFLICT (account_id) DO UPDATE SET message_count = account_message_totals.message_count + 1,
    captured_at = EXCLUDED.captured_at;
INSERT INTO group_account_message_totals (group_id, account_id, message_count, last_message_at, captured_at)
SELECT @GroupId, @AccountId, 1, @OccurredAt, @OccurredAt WHERE @GroupId IS NOT NULL
ON CONFLICT (group_id, account_id) DO UPDATE SET message_count = group_account_message_totals.message_count + 1,
    last_message_at = EXCLUDED.last_message_at, captured_at = EXCLUDED.captured_at;
INSERT INTO group_account_daily_analytics (group_id, account_id, day, message_count, first_message_at, last_message_at)
SELECT @GroupId, @AccountId, @Day, 1, @OccurredAt, @OccurredAt WHERE @GroupId IS NOT NULL
ON CONFLICT (group_id, account_id, day) DO UPDATE SET message_count = group_account_daily_analytics.message_count + 1,
    first_message_at = LEAST(group_account_daily_analytics.first_message_at, EXCLUDED.first_message_at),
    last_message_at = GREATEST(group_account_daily_analytics.last_message_at, EXCLUDED.last_message_at);
INSERT INTO group_user_daily_analytics (group_id, user_id, day, message_count, first_message_at, last_message_at)
SELECT @GroupId, @UserId, @Day, 1, @OccurredAt, @OccurredAt WHERE @GroupId IS NOT NULL
ON CONFLICT (group_id, user_id, day) DO UPDATE SET message_count = group_user_daily_analytics.message_count + 1,
    first_message_at = LEAST(group_user_daily_analytics.first_message_at, EXCLUDED.first_message_at),
    last_message_at = GREATEST(group_user_daily_analytics.last_message_at, EXCLUDED.last_message_at);
INSERT INTO group_daily_analytics (group_id, day, message_count, active_user_count, join_count, leave_count)
SELECT @GroupId, @Day, 1, 1, 0, 0 WHERE @GroupId IS NOT NULL
ON CONFLICT (group_id, day) DO UPDATE SET message_count = group_daily_analytics.message_count + 1,
    active_user_count = GREATEST(group_daily_analytics.active_user_count,
        (SELECT COUNT(*) FROM group_account_daily_analytics ga
         WHERE ga.group_id = @GroupId AND ga.day = @Day))",
                new { Edition = edition, principal.AccountId, UserId = principal.UserId,
                    GroupId = resolvedGroupId, OccurredAt = occurredAt, Day = DateOnly.FromDateTime(occurredAt.UtcDateTime) },
                transaction, cancellationToken: token));
            // bot:general is bot-wide with no group dimension. Section 12.2 records the decision to
            // freeze it in Redis for the duration rather than reconcile it, so the mirror carries
            // only the user and per-group totals.
            if (StorageRouting.RedisMirrorEnabled)
            {
                // Project the resulting canonical values, not the delta. Replaying an increment
                // after a crash would double-count; an absolute value converges. Read inside the
                // same transaction so the mirrored figure matches what was just committed.
                var userMessages = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT message_count FROM account_message_totals WHERE account_id = @AccountId",
                    new { principal.AccountId }, transaction, cancellationToken: token));
                long? groupMessages = resolvedGroupId == null
                    ? null
                    : await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
SELECT message_count FROM group_account_message_totals
 WHERE group_id = @GroupId AND account_id = @AccountId",
                        new { GroupId = resolvedGroupId, principal.AccountId }, transaction,
                        cancellationToken: token));
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "statistics", "statistics:global",
                    "increment", new { edition, userId, groupId, occurredAt, userMessages, groupMessages }, token);
            }
            await transaction.CommitAsync(token);
        }

        /// <summary>
        /// Applies last-seen timestamps for a whole batch in one statement.
        /// <para>
        /// This replaced two per-row upserts covering 4,752,053 user totals and 7,383,778 per-group
        /// totals. Each was a connection, a transaction and two principal resolutions, so the pair
        /// cost roughly 12.1M transactions - the single reason this domain had never completed a
        /// run. The counts themselves are now loaded set-wise by <c>Storage/Loading</c>, which also
        /// computes the unattributed residual; the per-row upsert overwrote <c>message_count</c>
        /// without recomputing that residual, so it could silently break the invariant.
        /// </para>
        /// <para>
        /// Only rows that already exist are touched. A last-seen timestamp for a user with no
        /// message total is not evidence of a total, and inventing a zero row would be inventing
        /// data.
        /// </para>
        /// </summary>
        internal async Task<int> ApplyLastSeenAsync(IReadOnlyCollection<GroupUserLastSeen> values,
            CancellationToken token = default)
        {
            if (values.Count == 0) return 0;
            await using var connection = await Database.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await connection.ExecuteAsync(new CommandDefinition(@"
CREATE TEMP TABLE stage_last_seen (telegram_chat_id bigint, telegram_user_id bigint,
    last_message_at timestamptz) ON COMMIT DROP", transaction: transaction, cancellationToken: token));

            await using (var writer = await connection.BeginBinaryImportAsync(
                             "COPY stage_last_seen (telegram_chat_id, telegram_user_id, last_message_at) " +
                             "FROM STDIN (FORMAT BINARY)", token))
            {
                foreach (var value in values)
                {
                    await writer.StartRowAsync(token);
                    await writer.WriteAsync(value.GroupId, NpgsqlTypes.NpgsqlDbType.Bigint, token);
                    await writer.WriteAsync(value.UserId, NpgsqlTypes.NpgsqlDbType.Bigint, token);
                    await writer.WriteAsync(value.LastMessageAt.UtcDateTime,
                        NpgsqlTypes.NpgsqlDbType.TimestampTz, token);
                }
                await writer.CompleteAsync(token);
            }

            var applied = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_account_message_totals t
   SET last_message_at = s.last_message_at
  FROM stage_last_seen s
  JOIN telegram_accounts a ON a.telegram_user_id = s.telegram_user_id
  JOIN telegram_chats c ON c.telegram_chat_id = s.telegram_chat_id
  JOIN groups g ON g.chat_id = c.chat_id
 WHERE t.group_id = g.group_id AND t.account_id = a.account_id
   AND t.last_message_at IS DISTINCT FROM s.last_message_at",
                transaction: transaction, cancellationToken: token));
            await transaction.CommitAsync(token);
            return applied;
        }

        /// <summary>
        /// Seeds the frozen <c>bot:general</c> counters as this edition's starting baseline, and
        /// reports whether it actually landed.
        /// <para>
        /// Deliberately <c>DO NOTHING</c> rather than an overwrite. <see cref="IncrementMessageAsync"/>
        /// accumulates <c>message_count</c> on top of the baseline, so an absolute upsert here would
        /// reset that progress every time a backfill ran with global data included - the counter
        /// would sawtooth. The Redis hash is frozen (section 12.2), so there is never a newer value
        /// to carry across.
        /// </para>
        /// <para>
        /// The corollary is an ordering requirement: seed before runtime traffic accumulates, or the
        /// row already exists and the frozen figure is not carried. The return value makes that
        /// visible rather than silent. This is tolerable because the counter has no reader and is
        /// recomputed at cutover regardless.
        /// </para>
        /// </summary>
        /// <returns>True when the baseline was seeded; false when one already existed.</returns>
        internal async Task<bool> UpsertAsync(BotStatisticBaseline value, CancellationToken token = default)
        {
            await using var connection = await Database.OpenConnectionAsync(token);
            return await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO bot_statistic_baselines (edition, message_count, user_count, ban_count, kick_count, captured_at)
VALUES (@Edition, @Messages, @Users, @Bans, @Kicks, @CapturedAt)
ON CONFLICT (edition) DO NOTHING", value,
                cancellationToken: token)) == 1;
        }

    }

    internal sealed class AuditEventRepository : OperationRepository
    {
        internal AuditEventRepository(PostgresDatabase database) : base(database) { }
        internal async Task<long> AppendAsync(AuditEvent value, CancellationToken token = default)
        {
            if (!value.GroupId.HasValue) return 0;
            await using var connection = await Database.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            var groupId = await OperationsPrincipalResolver.ResolveGroupAsync(connection, transaction,
                value.GroupId.Value, value.OccurredAt, token);
            long? actor = null;
            long? subject = null;
            if (value.ActorUserId.HasValue)
                actor = (await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                    value.ActorUserId.Value, value.OccurredAt, token)).UserId;
            if (value.SubjectUserId.HasValue)
                subject = (await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                    value.SubjectUserId.Value, value.OccurredAt, token)).UserId;
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO group_audit_events (group_id, actor_user_id, subject_user_id, event_type, occurred_at, legacy_key)
VALUES (@GroupId, @ActorUserId, @SubjectUserId, @EventType, @OccurredAt, @LegacyKey)
ON CONFLICT (legacy_key) WHERE legacy_key IS NOT NULL DO UPDATE SET legacy_key = EXCLUDED.legacy_key
RETURNING event_id", new { GroupId = groupId, ActorUserId = actor, SubjectUserId = subject,
                    value.EventType, value.OccurredAt, value.LegacyKey }, transaction, cancellationToken: token));
            await transaction.CommitAsync(token);
            return id;
        }
    }
}
