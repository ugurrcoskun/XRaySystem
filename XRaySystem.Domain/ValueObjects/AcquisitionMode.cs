namespace XRaySystem.Domain.ValueObjects;

public enum SnapFrameMode
{
    Raw = 0,
    Low = 1
}

public enum AcquisitionOperation
{
    Snap = 0,
    Grab = 1
}

public sealed record SnapRequest(double ExposureTimeMs, SnapFrameMode FrameMode)
{
    public void EnsureValid()
    {
        if (!double.IsFinite(ExposureTimeMs) || ExposureTimeMs is < 1 or > 3000)
            throw new ArgumentOutOfRangeException(
                nameof(ExposureTimeMs),
                "Exposure süresi 1-3000 ms aralığında olmalıdır.");
        if (!Enum.IsDefined(FrameMode))
            throw new ArgumentOutOfRangeException(nameof(FrameMode));
    }
}
