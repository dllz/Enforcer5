using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Enforcer5.Storage.Operations
{
    internal readonly record struct OperationsUserPrincipal(long AccountId, long UserId);

    internal static class OperationsPrincipalResolver
    {
        internal static async Task<OperationsUserPrincipal> ResolveUserAsync(DbConnection connection,
            DbTransaction transaction, long telegramUserId, DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            var value = await PrincipalResolver.EnsureUserAsync(connection, transaction, telegramUserId,
                cancellationToken: cancellationToken);
            return new OperationsUserPrincipal(value.AccountId, value.UserId);
        }

        internal static async Task<long> ResolveGroupAsync(DbConnection connection, DbTransaction transaction,
            long telegramGroupId, DateTimeOffset observedAt, CancellationToken cancellationToken = default) =>
            (await PrincipalResolver.EnsureGroupAsync(connection, transaction, telegramGroupId,
                chatType: "supergroup", cancellationToken: cancellationToken)).GroupId;
    }
}
