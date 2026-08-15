using Enforcer5.Storage.Cutover;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The barrier exists because the migration needs a moment when nothing is mutating Redis. The
/// motivating case is tempban expiry, which unbans with a hard delete and can otherwise be
/// re-inserted by a concurrently running snapshot loader.
/// </summary>
[Collection("QuiesceBarrier")]
public sealed class QuiesceBarrierTests : IDisposable
{
    // QuiesceBarrier is process-global static state. Test classes in this assembly have been shown
    // to interleave regardless of xUnit's collection configuration, so serialise explicitly rather
    // than trusting the runner.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public QuiesceBarrierTests()
    {
        Gate.Wait();
        QuiesceBarrier.ResetForTests();
    }

    public void Dispose()
    {
        try
        {
            QuiesceBarrier.Resume();
            QuiesceBarrier.ResetForTests();
        }
        finally
        {
            Gate.Release();
        }
    }

    [Fact]
    public async Task QuiesceReachesQuietWhenNoWorkIsInFlight()
    {
        QuiesceBarrier.Register("idle-writer", WriterResumeClass.Time);

        var report = await QuiesceBarrier.QuiesceAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Quiet);
        Assert.Empty(report.Stragglers);
        Assert.True(QuiesceBarrier.IsQuiescing);
    }

    [Fact]
    public async Task QuiesceWaitsForWorkAlreadyInFlight()
    {
        var writer = QuiesceBarrier.Register("busy-writer", WriterResumeClass.Time);
        var work = writer.BeginWork();
        Assert.Equal(1, writer.InFlight);

        var quiesce = QuiesceBarrier.QuiesceAsync(TimeSpan.FromSeconds(10));

        // The barrier must not report quiet while the unit is still open.
        var settledEarly = await Task.WhenAny(quiesce, Task.Delay(250)) == quiesce;
        Assert.False(settledEarly);

        work.Dispose();
        var report = await quiesce;

        Assert.True(report.Quiet);
        Assert.Equal(0, writer.InFlight);
    }

    [Fact]
    public async Task NewWorkIsRefusedWhileQuiescedAndProceedsAfterResume()
    {
        var writer = QuiesceBarrier.Register("gated-writer", WriterResumeClass.Time);
        var report = await QuiesceBarrier.QuiesceAsync(TimeSpan.FromSeconds(5));
        Assert.True(report.Quiet);

        var started = false;
        var attempt = Task.Run(() =>
        {
            using var work = writer.BeginWork();
            started = true;
        });

        // Blocked at the barrier, so the unit must not have begun.
        Assert.False(await Task.WhenAny(attempt, Task.Delay(250)) == attempt);
        Assert.False(started);

        QuiesceBarrier.Resume();
        await attempt.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(started);
        Assert.False(QuiesceBarrier.IsQuiescing);
    }

    [Fact]
    public async Task StragglersAreReportedRatherThanBlockingForever()
    {
        var writer = QuiesceBarrier.Register("straggler", WriterResumeClass.Cursor);
        using var stuck = writer.BeginWork();

        var report = await QuiesceBarrier.QuiesceAsync(TimeSpan.FromMilliseconds(300));

        Assert.False(report.Quiet);
        var straggler = Assert.Single(report.Stragglers);
        Assert.Equal("straggler", straggler.Name);
        Assert.Equal(1, straggler.InFlightAtTimeout);
    }

    [Fact]
    public async Task RegistrationIsIdempotentByName()
    {
        var first = QuiesceBarrier.Register("repeat", WriterResumeClass.Time);
        var second = QuiesceBarrier.Register("repeat", WriterResumeClass.Time);

        Assert.Same(first, second);
        Assert.Single(QuiesceBarrier.Registered);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task QuiesceReportsPerWriterSoTheSlowOneCanBeIdentified()
    {
        QuiesceBarrier.Register("fast", WriterResumeClass.Time);
        var slow = QuiesceBarrier.Register("slow", WriterResumeClass.Cursor);
        var work = slow.BeginWork();

        var quiesce = QuiesceBarrier.QuiesceAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        work.Dispose();
        var report = await quiesce;

        Assert.True(report.Quiet);
        Assert.Equal(2, report.Writers.Count);
        var slowReport = report.Writers.Single(w => w.Name == "slow");
        Assert.True(slowReport.TimeToQuiet > TimeSpan.FromMilliseconds(200),
            $"expected the slow writer to dominate, got {slowReport.TimeToQuiet}");
    }

    [Fact]
    public async Task ConcurrentQuiesceIsRejectedRatherThanCorruptingState()
    {
        QuiesceBarrier.Register("solo", WriterResumeClass.Time);
        await QuiesceBarrier.QuiesceAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => QuiesceBarrier.QuiesceAsync(TimeSpan.FromSeconds(5)));
    }
}
