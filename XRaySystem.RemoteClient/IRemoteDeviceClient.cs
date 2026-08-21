using XRaySystem.Contracts;

namespace XRaySystem.RemoteClient;

public interface IRemoteDeviceClient
{
    event Action<DeviceStateContract>? StatusChanged;
    event Action<TelemetryEventContract>? TelemetryReceived;
    event Action<RemoteLowFrame>? LowFrameReceived;
    event Action<SnapProgressEventContract>? SnapProgressReceived;
    event Action<DeviceErrorEventContract>? ErrorReceived;
    event Action<bool>? ConnectionStateChanged;

    bool EventsConnected { get; }
    Task ConnectEventsAsync(CancellationToken cancellationToken = default);
    Task DisconnectEventsAsync(CancellationToken cancellationToken = default);
    Task<DeviceStateContract> GetStateAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> ConnectDeviceAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> DisconnectDeviceAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> StartLiveAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> StopLiveAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> UpdateParametersAsync(
        AcquisitionParametersContract parameters,
        string profile,
        CancellationToken cancellationToken = default);
    Task<SnapCommandResult> SnapAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteImageMetadataContract>> GetImagesAsync(
        CancellationToken cancellationToken = default);
    Task<byte[]> GetLowImageAsync(Guid imageId, CancellationToken cancellationToken = default);
    Task<byte[]> GetRawImageAsync(Guid imageId, CancellationToken cancellationToken = default);
}
