using System;
using System.Collections.Generic;

namespace Enforcer5.Storage.Moderation
{
    internal static class GroupRoles
    {
        internal const string Authorized = "authorized";
        internal const string Moderator = "moderator";
    }

    internal static class GroupUserPolicyNames
    {
        internal const string WatchDisabled = "watch_disabled";
        internal const string ReportsBlocked = "reports_blocked";
        internal const string AdminNotificationsDisabled = "admin_notifications_disabled";
        internal const string TagAllSubscribed = "tag_all_subscribed";
    }

    internal static class WarningTypes
    {
        internal const string Standard = "standard";
        internal const string Prewarning = "prewarning";
        internal const string Media = "media";
    }

    internal static class WarningOperations
    {
        internal const string Add = "add";
        internal const string Remove = "remove";
        internal const string Reset = "reset";
    }

    internal sealed record GroupRoleAssignment(
        long GroupId, long UserId, string Role, DateTimeOffset AssignedAt, DateTimeOffset? RevokedAt);

    internal sealed record GroupElevationBlock(
        long GroupId, long UserId, DateTimeOffset BlockedAt, DateTimeOffset? RevokedAt);

    internal sealed record GroupUserPolicy(
        long GroupId, long UserId, string Policy, bool Enabled, DateTimeOffset UpdatedAt);

    internal sealed record WarningBalance(
        long GroupId, long UserId, string WarningType, long ActiveQuantity,
        long LifetimeQuantity, DateTimeOffset UpdatedAt);

    internal sealed record WarningEvent(
        long Id, long GroupId, long UserId, string WarningType, string Operation,
        long Quantity, long ActiveQuantity, long LifetimeQuantity, DateTimeOffset CreatedAt);

    internal sealed record WarningIssueResult(long Balance, long Quantity, long LifetimeQuantity);

    internal readonly record struct WarningDecision(long Balance, long Quantity)
    {
        internal static WarningDecision IssuePrewarning(long currentBalance)
        {
            var balance = Math.Max(0, currentBalance);
            return new WarningDecision(balance + 1, 1);
        }

        internal static WarningDecision IssueWarning(long currentBalance, long prewarningBalance)
        {
            var balance = Math.Max(0, currentBalance);
            var quantity = 1 + Math.Max(0, prewarningBalance);
            return new WarningDecision(balance + quantity, quantity);
        }

        internal static long Decrement(long currentBalance, long quantity = 1) =>
            Math.Max(0, Math.Max(0, currentBalance) - Math.Max(0, quantity));

        internal static long Reset(long currentBalance) => 0;
    }

    internal sealed class ModerationSnapshot
    {
        internal ModerationSnapshot(
            IReadOnlyList<GroupRoleAssignment> roles,
            IReadOnlyList<GroupElevationBlock> elevationBlocks,
            IReadOnlyList<GroupUserPolicy> policies,
            IReadOnlyList<WarningBalance> warningBalances)
        {
            Roles = roles;
            ElevationBlocks = elevationBlocks;
            Policies = policies;
            WarningBalances = warningBalances;
        }

        internal IReadOnlyList<GroupRoleAssignment> Roles { get; }
        internal IReadOnlyList<GroupElevationBlock> ElevationBlocks { get; }
        internal IReadOnlyList<GroupUserPolicy> Policies { get; }
        internal IReadOnlyList<WarningBalance> WarningBalances { get; }
    }

    internal sealed class ModerationBackfillResult
    {
        internal int RolesWritten { get; set; }
        internal int ElevationBlocksWritten { get; set; }
        internal int PoliciesWritten { get; set; }
        internal int WarningBalancesWritten { get; set; }
    }
}
