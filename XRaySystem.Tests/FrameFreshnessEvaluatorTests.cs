using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Tests;

public sealed class FrameFreshnessEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Missing_frame_is_unavailable()
    {
        var result = Evaluate(null);

        Assert.Equal(FrameFreshnessState.Unavailable, result.State);
        Assert.Equal(TimeSpan.Zero, result.Age);
    }

    [Fact]
    public void Threshold_is_inclusive_and_older_frame_is_stale()
    {
        Assert.Equal(FrameFreshnessState.Fresh, Evaluate(Now.AddSeconds(-5)).State);
        Assert.Equal(FrameFreshnessState.Stale, Evaluate(Now.AddSeconds(-5.001)).State);
    }

    [Fact]
    public void Small_future_skew_is_clamped_but_large_skew_is_rejected()
    {
        var tolerated = Evaluate(Now.AddSeconds(1));
        var invalid = Evaluate(Now.AddSeconds(3));

        Assert.Equal(FrameFreshnessState.Fresh, tolerated.State);
        Assert.Equal(TimeSpan.Zero, tolerated.Age);
        Assert.Equal(FrameFreshnessState.FutureClockSkew, invalid.State);
    }

    private static FrameFreshnessResult Evaluate(DateTimeOffset? capturedAt) =>
        FrameFreshnessEvaluator.Evaluate(
            capturedAt,
            Now,
            staleAfter: TimeSpan.FromSeconds(5),
            maximumFutureClockSkew: TimeSpan.FromSeconds(2));
}
