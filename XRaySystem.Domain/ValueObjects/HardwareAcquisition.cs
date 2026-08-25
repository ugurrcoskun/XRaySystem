namespace XRaySystem.Domain.ValueObjects;

/// <summary>
/// Internal device-side state. This is intentionally more detailed than the
/// client-facing DeviceStatus so radiation and detector ownership are not
/// collapsed into a single "capturing" flag.
/// </summary>
public enum AcquisitionCoordinatorState
{
    Disconnected,
    Idle,
    Configuring,
    Preparing,
    Armed,
    Exposing,
    ReadingOut,
    Grabbing,
    Freezing,
    Aborting,
    Frozen,
    Faulted
}

public sealed record AcquisitionHardwareState(
    AcquisitionCoordinatorState CoordinatorState,
    bool GeneratorConnected,
    bool DetectorConnected,
    bool XRayActive,
    bool DetectorAcquiring,
    Guid? ActiveAcquisitionId,
    bool IsVerified = true,
    DateTimeOffset? VerifiedAtUtc = null);

public sealed record GeneratorTechnique(double VoltageKv, double PowerW)
{
    public void EnsureValid()
    {
        if (!double.IsFinite(VoltageKv) || VoltageKv <= 0)
            throw new ArgumentOutOfRangeException(nameof(VoltageKv));
        if (!double.IsFinite(PowerW) || PowerW <= 0)
            throw new ArgumentOutOfRangeException(nameof(PowerW));
    }
}

public sealed record DetectorSnapSettings(double ExposureTimeMs, SnapFrameMode FrameMode)
{
    public void EnsureValid()
    {
        if (!double.IsFinite(ExposureTimeMs) || ExposureTimeMs is < 1 or > 3000)
            throw new ArgumentOutOfRangeException(nameof(ExposureTimeMs));
        if (!Enum.IsDefined(FrameMode))
            throw new ArgumentOutOfRangeException(nameof(FrameMode));
    }
}

public sealed record DetectorGrabSettings(double ExposureTimeMs)
{
    public void EnsureValid()
    {
        if (!double.IsFinite(ExposureTimeMs) || ExposureTimeMs is < 1 or > 3000)
            throw new ArgumentOutOfRangeException(nameof(ExposureTimeMs));
    }
}

/// <summary>
/// Metadata-only notification. Pixels are copied through IXRayDetector while
/// the vendor buffer token is valid, then the token is released explicitly.
/// </summary>
public sealed record DetectorFrameToken(
    Guid BufferTokenId,
    Guid AcquisitionId,
    long SequenceNumber,
    int Width,
    int Height,
    int StrideBytes,
    Gray16PixelFormat PixelFormat,
    string SourceMarker);

public sealed record GeneratorTelemetry(
    double VoltageKv,
    double TubeCurrentMa,
    double XRayIntensity,
    double ExposureTimeMs,
    MeasurementProvenance VoltageProvenance = MeasurementProvenance.Unknown,
    MeasurementProvenance TubeCurrentProvenance = MeasurementProvenance.Unknown,
    MeasurementProvenance ExposureTimeProvenance = MeasurementProvenance.Unknown);

public sealed record AcquisitionSafetyOptions(
    TimeSpan GeneratorReadyTimeout,
    TimeSpan DetectorArmTimeout,
    TimeSpan ExposureStartTimeout,
    TimeSpan MaximumExposureDuration,
    TimeSpan MaximumGrabDuration,
    TimeSpan ExposureStopConfirmationTimeout,
    TimeSpan DetectorStopTimeout,
    TimeSpan FrameReadyTimeout,
    TimeSpan BufferCopyTimeout)
{
    public static AcquisitionSafetyOptions Default { get; } = new(
        GeneratorReadyTimeout: TimeSpan.FromSeconds(5),
        DetectorArmTimeout: TimeSpan.FromSeconds(5),
        ExposureStartTimeout: TimeSpan.FromSeconds(2),
        MaximumExposureDuration: TimeSpan.FromSeconds(5),
        MaximumGrabDuration: TimeSpan.FromMinutes(1),
        ExposureStopConfirmationTimeout: TimeSpan.FromSeconds(2),
        DetectorStopTimeout: TimeSpan.FromSeconds(2),
        FrameReadyTimeout: TimeSpan.FromSeconds(10),
        BufferCopyTimeout: TimeSpan.FromSeconds(5));

    public void EnsureValid()
    {
        EnsurePositive(GeneratorReadyTimeout, nameof(GeneratorReadyTimeout));
        EnsurePositive(DetectorArmTimeout, nameof(DetectorArmTimeout));
        EnsurePositive(ExposureStartTimeout, nameof(ExposureStartTimeout));
        EnsurePositive(MaximumExposureDuration, nameof(MaximumExposureDuration));
        EnsurePositive(MaximumGrabDuration, nameof(MaximumGrabDuration));
        EnsurePositive(ExposureStopConfirmationTimeout, nameof(ExposureStopConfirmationTimeout));
        EnsurePositive(DetectorStopTimeout, nameof(DetectorStopTimeout));
        EnsurePositive(FrameReadyTimeout, nameof(FrameReadyTimeout));
        EnsurePositive(BufferCopyTimeout, nameof(BufferCopyTimeout));
    }

    private static void EnsurePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name);
    }
}

public sealed class AcquisitionTimeoutException : TimeoutException
{
    public AcquisitionTimeoutException(string stage, TimeSpan timeout)
        : base($"Acquisition aşaması zaman aşımına uğradı: {stage} ({timeout.TotalMilliseconds:0} ms).")
    {
        Stage = stage;
        Timeout = timeout;
    }

    public string Stage { get; }
    public TimeSpan Timeout { get; }
}
