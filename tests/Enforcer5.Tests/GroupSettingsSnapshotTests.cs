using Enforcer5.Storage;
using Xunit;

namespace Enforcer5.Tests;

public sealed class GroupSettingsSnapshotTests
{
    [Fact]
    public void ChecksumDoesNotDependOnFieldOrder()
    {
        var first = new Dictionary<string, string>
        {
            ["Flood"] = "yes",
            ["MaxFlood"] = "5"
        };
        var second = new Dictionary<string, string>
        {
            ["MaxFlood"] = "5",
            ["Flood"] = "yes"
        };

        Assert.Equal(GroupSettingsSnapshot.CalculateChecksum(first),
            GroupSettingsSnapshot.CalculateChecksum(second));
    }

    [Fact]
    public void ChecksumSeparatesFieldsAndValues()
    {
        var first = new Dictionary<string, string> { ["ab"] = "c" };
        var second = new Dictionary<string, string> { ["a"] = "bc" };

        Assert.NotEqual(GroupSettingsSnapshot.CalculateChecksum(first),
            GroupSettingsSnapshot.CalculateChecksum(second));
    }

    [Fact]
    public void LegacyValuesArePreservedExactly()
    {
        var fields = new Dictionary<string, string>
        {
            ["Flood"] = "yes",
            ["Report"] = "yes",
            ["Flagged"] = "no",
            ["unknown"] = "MixedCase"
        };
        var snapshot = new GroupSettingsSnapshot(-1001, GroupSettingGroups.Settings,
            "chat:-1001:settings", fields, true, 1, DateTimeOffset.UtcNow);

        Assert.Equal("yes", snapshot.Fields["Flood"]);
        Assert.Equal("no", snapshot.Fields["Flagged"]);
        Assert.Equal("MixedCase", snapshot.Fields["unknown"]);
    }

    [Fact]
    public void OnlyAllowlistedHashesCanBeMutated()
    {
        Assert.True(GroupSettingGroups.IsSupported(GroupSettingGroups.Settings));
        Assert.True(GroupSettingGroups.IsSupported(GroupSettingGroups.Welcome));
        Assert.False(GroupSettingGroups.IsSupported("warns"));
        Assert.False(GroupSettingGroups.IsSupported("adminses"));
    }
}
