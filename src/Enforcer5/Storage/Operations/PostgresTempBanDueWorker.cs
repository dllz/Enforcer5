using System;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Operations
{
    internal sealed record TempBanDueWorkerResult(int Claimed, int Completed, int Released);

    internal sealed class PostgresTempBanDueWorker
    {
        private readonly SanctionRepository _sanctions;
        private readonly string _edition;
        private readonly string _workerId;
        private readonly Func<long, long, CancellationToken, Task> _enqueueUnban;

        internal PostgresTempBanDueWorker(PostgresDatabase database, string edition, string workerId)
            : this(new SanctionRepository(database), edition, workerId,
                async (groupId, userId, cancellationToken) =>
                {
                    var completionKey = await Bot.QueueUnbanTrackedAsync(groupId, userId);
                    await WaitForCompletionAsync(completionKey, cancellationToken);
                })
        {
        }

        private static async Task WaitForCompletionAsync(string completionKey,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await Redis.db.HashGetAsync(completionKey,
                    new StackExchange.Redis.RedisValue[] { "status", "error" });
                if (result[0] == "succeeded") return;
                if (result[0] == "failed")
                    throw new InvalidOperationException($"Telegram unban failed: {result[1]}");
                await Task.Delay(250, cancellationToken);
            }
        }

        internal PostgresTempBanDueWorker(SanctionRepository sanctions, string edition, string workerId,
            Func<long, long, CancellationToken, Task> enqueueUnban)
        {
            _sanctions = sanctions ?? throw new ArgumentNullException(nameof(sanctions));
            _edition = string.IsNullOrWhiteSpace(edition)
                ? throw new ArgumentException("Edition is required.", nameof(edition))
                : edition;
            _workerId = string.IsNullOrWhiteSpace(workerId)
                ? throw new ArgumentException("Worker id is required.", nameof(workerId))
                : workerId;
            _enqueueUnban = enqueueUnban ?? throw new ArgumentNullException(nameof(enqueueUnban));
        }

        internal async Task<TempBanDueWorkerResult> ProcessDueAsync(int limit,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var claims = await _sanctions.ClaimDueTempBansAsync(
                _edition, _workerId, limit, now, cancellationToken);
            var completed = 0;
            var released = 0;

            for (var index = 0; index < claims.Count; index++)
            {
                var claim = claims[index];
                try
                {
                    await _enqueueUnban(claim.GroupId, claim.UserId, cancellationToken);
                    if (!await _sanctions.CompleteClaimAsync(claim.Id, _workerId,
                            DateTimeOffset.UtcNow, CancellationToken.None))
                        throw new InvalidOperationException($"Tempban claim {claim.Id} is no longer owned by {_workerId}.");
                    completed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    released += await ReleaseRemainingAsync(claims, index);
                    throw;
                }
                catch (Exception e)
                {
                    if (await _sanctions.ReleaseClaimAsync(claim.Id, _workerId, CancellationToken.None))
                        released++;
                    LogHelper.Error($"Tempban claim {claim.Id} failed: {e.Message}");
                }
            }

            return new TempBanDueWorkerResult(claims.Count, completed, released);
        }

        private async Task<int> ReleaseRemainingAsync(System.Collections.Generic.IReadOnlyList<Sanction> claims,
            int startIndex)
        {
            var released = 0;
            for (var index = startIndex; index < claims.Count; index++)
                if (await _sanctions.ReleaseClaimAsync(claims[index].Id, _workerId, CancellationToken.None))
                    released++;
            return released;
        }
    }
}
