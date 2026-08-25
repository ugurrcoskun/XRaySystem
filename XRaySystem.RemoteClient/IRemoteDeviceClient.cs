using XRaySystem.Contracts;

namespace XRaySystem.RemoteClient;

public interface IRemoteDeviceClient
{
    event Action<DeviceStateContract>? StatusChanged;
    event Action<TelemetryEventContract>? TelemetryReceived;
    event Action<RemoteLowFrame>? LowFrameReceived;
    event Action<SnapProgressEventContract>? SnapProgressReceived;
    event Action<HardwareStateEventContract>? HardwareStateReceived;
    event Action<FrameReadyEventContract>? FrameReadyReceived;
    event Action<GrabFrameReadyEventContract>? GrabFrameReadyReceived;
    event Action<GrabFrozenEventContract>? GrabFrozenReceived;
    event Action<ExposureEndedEventContract>? ExposureEndedReceived;
    event Action<DeviceErrorEventContract>? ErrorReceived;
    event Action<bool>? ConnectionStateChanged;

    bool EventsConnected { get; }
    Task ValidateProtocolAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task ConnectEventsAsync(CancellationToken cancellationToken = default);
    Task DisconnectEventsAsync(CancellationToken cancellationToken = default);
    Task<DeviceStateContract> GetStateAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> ConnectDeviceAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> DisconnectDeviceAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> StartLiveAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> StopLiveAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> StartGrabAsync(CancellationToken cancellationToken = default) =>
        StartLiveAsync(cancellationToken);
    Task<DeviceCommandResult> FreezeGrabAsync(CancellationToken cancellationToken = default) =>
        StopLiveAsync(cancellationToken);
    Task<DeviceCommandResult> AbortAsync(CancellationToken cancellationToken = default) =>
        StopLiveAsync(cancellationToken);
    Task<DeviceCommandResult> UpdateParametersAsync(
        AcquisitionParametersContract parameters,
        string profile,
        CancellationToken cancellationToken = default);
    Task<SnapCommandResult> SnapAsync(CancellationToken cancellationToken = default);
    Task<SnapCommandResult> SnapAsync(
        SnapFrameModeContract frameMode,
        CancellationToken cancellationToken = default)
    {
        if (frameMode != SnapFrameModeContract.Raw)
            throw new NotSupportedException("Bu remote istemci SNAP LOW modunu desteklemiyor.");
        return SnapAsync(cancellationToken);
    }
    Task<IReadOnlyList<RemoteImageMetadataContract>> GetImagesAsync(
        CancellationToken cancellationToken = default);
    Task<byte[]> GetLowImageAsync(Guid imageId, CancellationToken cancellationToken = default);
    Task<byte[]> GetRawImageAsync(Guid imageId, CancellationToken cancellationToken = default);
}
