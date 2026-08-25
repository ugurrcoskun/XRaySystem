namespace XRaySystem.Domain.ValueObjects;

public enum FrameFreshnessState
{
    Unavailable,
    Fresh,
    Stale,
    FutureClockSkew
}

public sealed record FrameFreshnessResult(FrameFreshnessState State, TimeSpan Age);

public static class FrameFreshnessEvaluator
{
    public static FrameFreshnessResult Evaluate(
        DateTimeOffset? capturedAtUtc,
        DateTimeOffset nowUtc,
        TimeSpan staleAfter,
        TimeSpan maximumFutureClockSkew)
    {
        if (staleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleAfter));
        if (maximumFutureClockSkew < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumFutureClockSkew));
        if (capturedAtUtc is null)
            return new FrameFreshnessResult(FrameFreshnessState.Unavailable, TimeSpan.Zero);

        var age = nowUtc.ToUniversalTime() - capturedAtUtc.Value.ToUniversalTime();
        if (age < -maximumFutureClockSkew)
            return new FrameFreshnessResult(FrameFreshnessState.FutureClockSkew, age);
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return new FrameFreshnessResult(
            age > staleAfter ? FrameFreshnessState.Stale : FrameFreshnessState.Fresh,
            age);
    }
}
