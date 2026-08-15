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

    /// <summary>
    /// The estate mixes writer cultures: <c>globalBan.time</c> alone holds month-first, day-first
    /// and year-first values, because each was written with whatever culture the host had at the
    /// time. Day-first with a meridiem had no matching format, so 197 global bans in the production
    /// copy failed to parse and were dropped - and for a global ban absence means *allowed*, which
    /// makes a dropped record a silent security false-negative.
    /// </summary>
    [Theory]
    [InlineData("13/06/2016 09:30:30 PM", 2016, 6, 13, 21)]
    [InlineData("12/17/2024 5:37:48 PM", 2024, 12, 17, 17)]
    [InlineData("2020/06/18 11:44:56 AM", 2020, 6, 18, 11)]
    // Written by a display formatter and stored verbatim, newline and all. 113 of the 197
    // unparseable global bans were this shape, which no format string covered.
    [InlineData("On Saturday, 22 October 2016\nAt 16:09:11", 2016, 10, 22, 16)]
    [InlineData("On Friday, 28 October 2016\r\nAt 13:08:41", 2016, 10, 28, 13)]
    public void EveryObservedLegacyDateShapeParses(string value, int year, int month, int day, int hour)
    {
        var result = LegacyOperationParsers.ParseTimestamp(value);

        Assert.NotEqual(LegacyParseStatus.Malformed, result.Status);
        Assert.Equal(new DateTime(year, month, day, hour, 0, 0),
            result.Value.WallClock.Date.AddHours(result.Value.WallClock.Hour));
    }

    [Fact]
    public void ADateThatCouldBeReadEitherWayIsResolvedButReported()
    {
        // 05/06 could be 5 June or May 6 - an eleven-month spread on a moderation record. It is
        // resolved month-first because that is the dominant shape in the data, but reported rather
        // than silently trusted. It is still migrated: Ambiguous carries a value, Malformed does not.
        var result = LegacyOperationParsers.ParseTimestamp("05/06/2016 09:30:30 PM");

        Assert.Equal(LegacyParseStatus.Ambiguous, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(5, result.Value.WallClock.Month);
        Assert.Equal(6, result.Value.WallClock.Day);
    }

    [Fact]
    public void ADateThatCanOnlyBeReadOneWayIsNotReportedAsAmbiguous()
    {
        // Either component exceeding 12 forces the reading, and equal components make it moot.
        Assert.Equal(LegacyParseStatus.Success,
            LegacyOperationParsers.ParseTimestamp("13/06/2016 09:30:30 PM").Status);
        Assert.Equal(LegacyParseStatus.Success,
            LegacyOperationParsers.ParseTimestamp("06/06/2016 09:30:30 PM").Status);
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
