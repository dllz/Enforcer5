using Enforcer5.Storage.Moderation;
using Xunit;

namespace Enforcer5.Tests;

public sealed class ModerationLegacyMappingTests
{
    [Fact]
    public void LegacySetsMapToCanonicalRolesBlockAndPolicies()
    {
        var at = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        var snapshot = LegacyModerationMapper.MapGroup(-1001,
            new[] { "10", "bad" }, new[] { "20" }, new[] { "30" }, new[] { "40" },
            new[] { "50" }, new[] { "60" }, new[] { "70" },
            new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, string>(), at);

        Assert.Collection(snapshot.Roles,
            role => Assert.Equal(new GroupRoleAssignment(-1001, 10, GroupRoles.Authorized, at, null), role),
            role => Assert.Equal(new GroupRoleAssignment(-1001, 20, GroupRoles.Moderator, at, null), role));
        Assert.Equal(new GroupElevationBlock(-1001, 30, at, null), Assert.Single(snapshot.ElevationBlocks));
        Assert.Collection(snapshot.Policies,
            policy => Assert.Equal(GroupUserPolicyNames.WatchDisabled, policy.Policy),
            policy => Assert.Equal(GroupUserPolicyNames.ReportsBlocked, policy.Policy),
            policy => Assert.Equal(GroupUserPolicyNames.AdminNotificationsDisabled, policy.Policy),
            policy => Assert.Equal(GroupUserPolicyNames.TagAllSubscribed, policy.Policy));
        Assert.All(snapshot.Policies, policy => Assert.True(policy.Enabled));
    }

    [Fact]
    public void WarningHashesMergeUsersAndPreserveActiveAndLifetimeMeaning()
    {
        var at = DateTimeOffset.UnixEpoch;
        var snapshot = LegacyModerationMapper.MapGroup(-1001,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string> { ["10"] = "2", ["20"] = "-4" },
            new Dictionary<string, string> { ["10"] = "7", ["20"] = "3" },
            new Dictionary<string, string> { ["10"] = "4", ["invalid"] = "9" },
            new Dictionary<string, string> { ["30"] = "5" }, at);

        Assert.Contains(new WarningBalance(-1001, 10, WarningTypes.Standard, 2, 7, at),
            snapshot.WarningBalances);
        Assert.Contains(new WarningBalance(-1001, 10, WarningTypes.Prewarning, 4, 4, at),
            snapshot.WarningBalances);
        Assert.Contains(new WarningBalance(-1001, 20, WarningTypes.Standard, 0, 3, at),
            snapshot.WarningBalances);
        Assert.Contains(new WarningBalance(-1001, 30, WarningTypes.Media, 5, 5, at),
            snapshot.WarningBalances);
        Assert.DoesNotContain(snapshot.WarningBalances, balance => balance.UserId == 0);
    }

    [Fact]
    public void MissingLifetimeFallsBackToCurrentStandardWarnings()
    {
        var snapshot = LegacyModerationMapper.MapGroup(-1001,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string> { ["10"] = "6" }, new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);

        Assert.Equal(6, Assert.Single(snapshot.WarningBalances).LifetimeQuantity);
    }
}
