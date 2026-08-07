using Enforcer5.Storage.Moderation;
using Xunit;

namespace Enforcer5.Tests;

public sealed class ModerationWarningDecisionTests
{
    [Fact]
    public void PrewarningIncrementsItsBalanceByOne()
    {
        Assert.Equal(new WarningDecision(4, 1), WarningDecision.IssuePrewarning(3));
    }

    [Fact]
    public void WarningConsumesPrewarningsIntoIssuedQuantity()
    {
        Assert.Equal(new WarningDecision(8, 4), WarningDecision.IssueWarning(4, 3));
    }

    [Fact]
    public void NegativeLegacyBalancesCannotProduceNegativeResults()
    {
        Assert.Equal(new WarningDecision(1, 1), WarningDecision.IssueWarning(-5, -2));
        Assert.Equal(0, WarningDecision.Decrement(-5));
        Assert.Equal(0, WarningDecision.Decrement(2, 10));
        Assert.Equal(0, WarningDecision.Reset(12));
    }

    [Fact]
    public void IssueResultExposesBalanceQuantityAndLifetime()
    {
        var result = new WarningIssueResult(7, 3, 19);

        Assert.Equal(7, result.Balance);
        Assert.Equal(3, result.Quantity);
        Assert.Equal(19, result.LifetimeQuantity);
    }
}
