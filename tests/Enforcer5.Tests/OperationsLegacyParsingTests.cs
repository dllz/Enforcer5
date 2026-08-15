using Enforcer5.Storage.Operations;
using Xunit;

namespace Enforcer5.Tests;

public sealed class OperationsLegacyParsingTests
{
    [Fact]
    public void TempBanParsesExpiryIdsAndEditionExactly()
    {
        var result = LegacyOperationParsers.ParseTempBan("1786104000",
            "-100123:456:User Name:Group Name", Editions.Premium);

        Assert.True(result.IsSuccess);
        Assert.Equal(-100123, result.Value.GroupId);
        Assert.Equal(456, result.Value.UserId);
        Assert.Equal("User Name", result.Value.UserName);
        Assert.Equal("Group Name", result.Value.GroupName);
        Assert.Equal(Editions.Premium, result.Value.Edition);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786104000), result.Value.ExpiresAt);
    }

    [Theory]
    [InlineData("not-time", "-100:42")]
    [InlineData("1786104000", "missing-user")]
    [InlineData("1786104000", "-100:not-user")]
    public void MalformedTempBanIsReturnedAsAnError(string expiry, string value)
    {
        var result = LegacyOperationParsers.ParseTempBan(expiry, value, Editions.Normal);

        Assert.Equal(LegacyParseStatus.Malformed, result.Status);
        Assert.IsType<FormatException>(result.Error);
    }

    [Fact]
    public void GlobalBanUsesCaseSensitiveNamedFieldsRatherThanFieldOrder()
    {
        var fields = new Dictionary<string, string>
        {
            ["unbanMotivation"] = "appeal",
            ["motivation"] = "spam",
            ["banned"] = "0",
            ["unbanTime"] = "2026-08-07T14:00:00+00:00",
            ["time"] = "2026-08-07T12:00:00+00:00"
        };

        var result = LegacyOperationParsers.ParseGlobalBan(42, fields, "globalBan:42");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Active);
        Assert.Equal("spam", result.Value.Reason);
        Assert.Equal("appeal", result.Value.RevocationReason);
        Assert.Equal(DateTimeOffset.Parse("2026-08-07T12:00:00+00:00"), result.Value.CreatedAt);
    }

    [Fact]
    public void WrongCaseGlobalBanFieldIsMalformed()
    {
        var result = LegacyOperationParsers.ParseGlobalBan(42,
            new Dictionary<string, string> { ["Banned"] = "1" }, "globalBan:42");

        Assert.Equal(LegacyParseStatus.Malformed, result.Status);
    }

    [Theory]
    [InlineData("0", "open")]
    [InlineData("1", "solved")]
    public void ReportSolvedFlagMapsToCanonicalStatus(string solved, string expected)
    {
        var fields = new Dictionary<string, string>
        {
            ["Solved"] = solved,
            ["Created"] = "2026-08-07T12:00:00Z"
        };
        if (solved == "1")
        {
            fields["SolvedAt"] = "2026-08-07T13:00:00Z";
            fields["solvedBy"] = "Moderator";
        }

        var result = LegacyOperationParsers.ParseReport(-100, 99, fields, "flagged:-100:99");

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Status);
    }

    [Fact]
    public void SolvedReportWithoutSolverMetadataIsMalformed()
    {
        var result = LegacyOperationParsers.ParseReport(-100, 99,
            new Dictionary<string, string> { ["Solved"] = "1" }, "flagged:-100:99");

        Assert.Equal(LegacyParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void OffsetlessLegacyTimeUsesConfiguredLegacyTimezone()
    {
        var result = LegacyOperationParsers.ParseTimestamp("01:02:03 07-08-2026");

        Assert.Equal(LegacyParseStatus.Success, result.Status);
        Assert.NotNull(result.Value.Instant);
        Assert.Equal(DateTimeKind.Unspecified, result.Value.WallClock.Kind);
    }

    [Fact]
    public void ExplicitOffsetLegacyTimeIsAnUnambiguousInstant()
    {
        var result = LegacyOperationParsers.ParseTimestamp("2026-08-07T12:00:00+02:00");

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero), result.Value.Instant);
    }

    [Fact]
    public void LocalDateTimeTicksUseConfiguredLegacyTimezone()
    {
        var ticks = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Unspecified).Ticks.ToString();

        var result = LegacyOperationParsers.ParseTicks(ticks);

        Assert.Equal(LegacyParseStatus.Success, result.Status);
        Assert.NotEqual(default, result.Value);
    }
}
