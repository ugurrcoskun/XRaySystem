using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Domain.Interfaces;

public interface IXRayGenerator
{
    bool IsConnected { get; }
    bool IsExposureActive { get; }

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task ApplyTechniqueAsync(GeneratorTechnique technique, CancellationToken cancellationToken);
    Task PrepareAsync(CancellationToken cancellationToken);
    Task WaitUntilReadyAsync(CancellationToken cancellationToken);
    Task StartExposureAsync(Guid acquisitionId, CancellationToken cancellationToken);
    Task StopExposureAsync(CancellationToken cancellationToken);
    Task WaitUntilExposureStoppedAsync(CancellationToken cancellationToken);
    Task<GeneratorTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken);
}

public interface IXRayDetector
{
    bool IsConnected { get; }
    bool IsAcquiring { get; }
    bool SupportsSnapMode(SnapFrameMode frameMode);

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task ConfigureSnapAsync(
        Guid acquisitionId,
        DetectorSnapSettings settings,
        CancellationToken cancellationToken);
    Task ConfigureGrabAsync(
        Guid grabSessionId,
        DetectorGrabSettings settings,
        CancellationToken cancellationToken);
    Task ArmAsync(Guid acquisitionId, CancellationToken cancellationToken);
    Task WaitUntilArmedAsync(Guid acquisitionId, CancellationToken cancellationToken);
    Task StartSnapAsync(Guid acquisitionId, CancellationToken cancellationToken);
    Task StartGrabAsync(Guid grabSessionId, CancellationToken cancellationToken);
    Task FreezeAsync(Guid grabSessionId, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<DetectorFrameToken> WaitForFrameAsync(CancellationToken cancellationToken);
    Task<ushort[]> CopyFrameAsync(DetectorFrameToken token, CancellationToken cancellationToken);
    Task ReleaseFrameAsync(DetectorFrameToken token, CancellationToken cancellationToken);
}

public interface IAcquisitionCoordinator : ISensorService
{
    AcquisitionCoordinatorState CoordinatorState { get; }
    AcquisitionHardwareState HardwareState { get; }
    event Action<AcquisitionHardwareState>? HardwareStateChanged;
}
