using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;
using Npgsql;

namespace Enforcer5.Data
{
    /// <summary>A /batch as confirmed: what to run, for whom, on which members.</summary>
    internal sealed record NewBatch(long ChatId, string Edition, string Kind, string Reason, int? DurationMinutes,
        string CommandText, long RequestedBy, long ConfirmedBy, int Skipped, IReadOnlyList<long> Members);

    internal sealed record BatchRecord(long BatchId, long ChatId, string Edition, string Kind, string Reason, int? DurationMinutes,
        string CommandText, long RequestedBy, long ConfirmedBy, string Status, int? ProgressMessageId, int Skipped, int Total);

    internal sealed record BatchItem(int Position, long MemberId);

    /// <summary>One member of a batch and how it ended for them (outcome and reason as <see cref="BatchTally"/>).</summary>
    internal sealed record BatchItemOutcome(long MemberId, string Outcome, string Detail);

    /// <summary>How many items ended one way (<see cref="Outcome"/>) for one reason (<see cref="Detail"/>).</summary>
    internal sealed record BatchTally(string Outcome, string Detail, int Count);

    /// <summary>The /batch runs (<c>batches</c>, <c>batch_items</c>; Actions/Batches).</summary>
    internal interface IBatchRepository
    {
        /// <summary>Stores a confirmed batch, running. Null when the group already has one running.</summary>
        Task<long?> CreateAsync(NewBatch batch);

        Task<BatchRecord> GetAsync(long batchId);

        /// <summary>The edition's running batches, to carry on after a restart.</summary>
        Task<IReadOnlyList<long>> RunningAsync(string edition);

        /// <summary>The next items of the batch that have not run, in list order.</summary>
        Task<IReadOnlyList<BatchItem>> PendingAsync(long batchId, int limit);

        Task SetOutcomeAsync(long batchId, int position, string outcome, string detail, DateTimeOffset at);

        Task SetProgressMessageAsync(long batchId, int messageId);

        /// <summary>
        /// Ends a running batch as <paramref name="status"/> (done or stopped); items that had not run are skipped.
        /// False when it was no longer running (stopped already, or done).
        /// </summary>
        Task<bool> FinishAsync(long batchId, string status, DateTimeOffset at);

        /// <summary>The items so far, counted by outcome and reason.</summary>
        Task<IReadOnlyList<BatchTally>> TallyAsync(long batchId);

        /// <summary>Every item that has an outcome, in list order: who the batch did what to.</summary>
        Task<IReadOnlyList<BatchItemOutcome>> OutcomesAsync(long batchId);
    }

    internal sealed class PostgresBatchRepository : IBatchRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresBatchRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private static DateTime Utc(DateTimeOffset at) => at.UtcDateTime;

        public async Task<long?> CreateAsync(NewBatch batch)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, batch.ChatId,
                chatType: PostgresBanHistoryRepository.ChatType(batch.ChatId));
            var requested = await PrincipalResolver.EnsureUserAsync(connection, transaction, batch.RequestedBy);
            var confirmed = batch.ConfirmedBy == batch.RequestedBy
                ? requested
                : await PrincipalResolver.EnsureUserAsync(connection, transaction, batch.ConfirmedBy);
            long id;
            try
            {
                await connection.ExecuteAsync(new CommandDefinition("SAVEPOINT batch", transaction: transaction));
                id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO batches (group_id, edition, kind, reason, duration_minutes, command_text, requested_by_account_id,
                     confirmed_by_account_id, skipped)
VALUES (@GroupId, @Edition, @Kind, @Reason, @DurationMinutes, @CommandText, @Requested, @Confirmed, @Skipped)
RETURNING batch_id",
                    new
                    {
                        group.GroupId, batch.Edition, batch.Kind, batch.Reason, batch.DurationMinutes, batch.CommandText,
                        Requested = requested.AccountId, Confirmed = confirmed.AccountId, batch.Skipped
                    }, transaction));
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // batches_one_running_idx: the group has one running already.
                await transaction.RollbackAsync();
                return null;
            }
            // One statement for the whole list: a list may hold thousands.
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO batch_items (batch_id, position, telegram_user_id)
SELECT @Id, m.position - 1, m.member FROM unnest(@Members) WITH ORDINALITY AS m(member, position)",
                new { Id = id, Members = batch.Members.ToArray() }, transaction));
            await transaction.CommitAsync();
            return id;
        }

        public async Task<BatchRecord> GetAsync(long batchId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<BatchRecord>(@"
SELECT b.batch_id AS BatchId, c.telegram_chat_id AS ChatId, b.edition AS Edition, b.kind AS Kind, b.reason AS Reason,
       b.duration_minutes AS DurationMinutes, b.command_text AS CommandText, r.telegram_user_id AS RequestedBy,
       f.telegram_user_id AS ConfirmedBy, b.status AS Status, b.progress_message_id AS ProgressMessageId, b.skipped AS Skipped,
       (SELECT count(*)::int FROM batch_items i WHERE i.batch_id = b.batch_id) AS Total
FROM batches b
JOIN groups g ON g.group_id = b.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts r ON r.account_id = b.requested_by_account_id
JOIN telegram_accounts f ON f.account_id = b.confirmed_by_account_id
WHERE b.batch_id = @BatchId", new { BatchId = batchId });
        }

        public async Task<IReadOnlyList<long>> RunningAsync(string edition)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<long>(
                "SELECT batch_id FROM batches WHERE edition = @Edition AND status = 'running' ORDER BY batch_id",
                new { Edition = edition })).ToList();
        }

        public async Task<IReadOnlyList<BatchItem>> PendingAsync(long batchId, int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<BatchItem>(@"
SELECT position AS Position, telegram_user_id AS MemberId FROM batch_items
WHERE batch_id = @BatchId AND outcome IS NULL ORDER BY position LIMIT @Limit",
                new { BatchId = batchId, Limit = limit })).ToList();
        }

        public async Task SetOutcomeAsync(long batchId, int position, string outcome, string detail, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(
                "UPDATE batch_items SET outcome = @Outcome, detail = @Detail, done_at = @At WHERE batch_id = @BatchId AND position = @Position",
                new { BatchId = batchId, Position = position, Outcome = outcome, Detail = detail, At = Utc(at) });
        }

        public async Task SetProgressMessageAsync(long batchId, int messageId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync("UPDATE batches SET progress_message_id = @MessageId WHERE batch_id = @BatchId",
                new { BatchId = batchId, MessageId = messageId });
        }

        public async Task<bool> FinishAsync(long batchId, string status, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var ended = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE batches SET status = @Status, finished_at = @At WHERE batch_id = @BatchId AND status = 'running'",
                new { BatchId = batchId, Status = status, At = Utc(at) }, transaction));
            if (ended > 0)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE batch_items SET outcome = 'skipped', done_at = @At WHERE batch_id = @BatchId AND outcome IS NULL",
                    new { BatchId = batchId, At = Utc(at) }, transaction));
            await transaction.CommitAsync();
            return ended > 0;
        }

        public async Task<IReadOnlyList<BatchTally>> TallyAsync(long batchId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<BatchTally>(@"
SELECT outcome AS Outcome, detail AS Detail, count(*)::int AS Count FROM batch_items
WHERE batch_id = @BatchId AND outcome IS NOT NULL GROUP BY outcome, detail ORDER BY outcome, detail",
                new { BatchId = batchId })).ToList();
        }

        public async Task<IReadOnlyList<BatchItemOutcome>> OutcomesAsync(long batchId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<BatchItemOutcome>(@"
SELECT telegram_user_id AS MemberId, outcome AS Outcome, detail AS Detail FROM batch_items
WHERE batch_id = @BatchId AND outcome IS NOT NULL ORDER BY position",
                new { BatchId = batchId })).ToList();
        }
    }
}
