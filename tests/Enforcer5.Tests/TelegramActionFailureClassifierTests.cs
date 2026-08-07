using System.Net.Http;
using Enforcer5.Helpers;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Xunit;

namespace Enforcer5.Tests;

public sealed class TelegramActionFailureClassifierTests
{
    [Fact]
    public void RateLimitUsesFullTelegramRetryAfter()
    {
        var exception = new ApiRequestException("Too Many Requests", 429,
            new ResponseParameters { RetryAfter = 125 });

        var result = TelegramActionFailureClassifier.Classify(exception, 1);

        Assert.Equal(TelegramActionFailureKind.Retry, result.Kind);
        Assert.Equal(TimeSpan.FromSeconds(125), result.RetryAfter);
    }

    [Fact]
    public void MissingDeleteIsIdempotentSuccess()
    {
        var exception = new ApiRequestException("Bad Request: message to delete not found", 400);

        var result = TelegramActionFailureClassifier.Classify(exception, 1);

        Assert.Equal(TelegramActionFailureKind.Success, result.Kind);
    }

    [Fact]
    public void ServerFailureIsRetriedWithBoundedBackoff()
    {
        var result = TelegramActionFailureClassifier.Classify(
            new ApiRequestException("Telegram unavailable", 503), 20);

        Assert.Equal(TelegramActionFailureKind.Retry, result.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), result.RetryAfter);
    }

    [Fact]
    public void NetworkFailureIsRetried()
    {
        var result = TelegramActionFailureClassifier.Classify(new HttpRequestException("offline"), 2);

        Assert.Equal(TelegramActionFailureKind.Retry, result.Kind);
        Assert.Equal(TimeSpan.FromSeconds(4), result.RetryAfter);
    }

    [Fact]
    public void PermanentClientFailureIsTerminal()
    {
        var result = TelegramActionFailureClassifier.Classify(
            new ApiRequestException("Bad Request: chat not found", 400), 1);

        Assert.Equal(TelegramActionFailureKind.Terminal, result.Kind);
    }
}
