using Enforcer5.Storage;
using Xunit;

namespace Enforcer5.Tests;

public sealed class MigrationCommandOptionsTests
{
    [Theory]
    [InlineData("group-settings")]
    [InlineData("principal-content")]
    [InlineData("moderation")]
    [InlineData("operations")]
    public void ParsesEachUnscopedDomain(string domain)
    {
        Assert.True(MigrationCommandOptionsParser.TryParse(
            ["migrate", domain, "backfill"], out var options));
        Assert.Equal(domain, options.Domain);
        Assert.False(options.Reconcile);
        Assert.Null(options.GroupId);
    }

    [Fact]
    public void ParsesExactScopedForm()
    {
        Assert.True(MigrationCommandOptionsParser.TryParse(
            ["MIGRATE", "OPERATIONS", "RECONCILE", "--group", "-100123"], out var options));
        Assert.Equal("operations", options.Domain);
        Assert.True(options.Reconcile);
        Assert.Equal(-100123, options.GroupId);
    }

    [Theory]
    [InlineData("migrate operations backfill --group 0")]
    [InlineData("migrate operations backfill --group nope")]
    [InlineData("migrate operations --group -100 backfill")]
    [InlineData("migrate unknown backfill")]
    [InlineData("migrate operations delete")]
    [InlineData("migrate operations backfill extra")]
    public void RejectsAnythingOutsideExactForms(string command)
    {
        Assert.False(MigrationCommandOptionsParser.TryParse(command.Split(' '), out var options));
        Assert.Null(options);
    }

    [Fact]
    public void GroupOptionIsCaseInsensitive()
    {
        Assert.True(MigrationCommandOptionsParser.TryParse(
            ["migrate", "operations", "backfill", "--GROUP", "-100"], out var options));
        Assert.Equal(-100, options.GroupId);
    }
}
