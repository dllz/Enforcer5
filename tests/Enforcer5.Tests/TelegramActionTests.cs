using Enforcer5.Models;
using Xunit;

namespace Enforcer5.Tests;

public sealed class TelegramActionTests
{
    [Fact]
    public void DeleteActionHasStableIdentityAndFifteenMinuteDeadline()
    {
        var now = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

        var action = TelegramAction.DeleteMessage(-100123, 456, now);

        Assert.False(string.IsNullOrWhiteSpace(action.Id));
        Assert.Equal(TelegramActionType.DeleteMessage, action.Type);
        Assert.Equal(-100123, action.ChatId);
        Assert.Equal(456, action.MessageId);
        Assert.Equal(now.ToUnixTimeMilliseconds(), action.CreatedAtUnixMs);
        Assert.Equal(now.AddMinutes(15).ToUnixTimeMilliseconds(), action.DeadlineUnixMs);
    }
}
