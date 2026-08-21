using Microsoft.AspNetCore.SignalR;
using XRaySystem.Contracts;

namespace XRaySystem.DeviceHost;

public interface IDeviceEventPublisher
{
    Task PublishSnapProgressAsync(SnapProgressEventContract progress, CancellationToken cancellationToken);
    Task PublishErrorAsync(DeviceErrorEventContract error, CancellationToken cancellationToken);
}

public sealed class SignalRDeviceEventPublisher(IHubContext<DeviceEventsHub> hub) : IDeviceEventPublisher
{
    public Task PublishSnapProgressAsync(
        SnapProgressEventContract progress,
        CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync(DeviceEventNames.SnapProgress, progress, cancellationToken);

    public Task PublishErrorAsync(
        DeviceErrorEventContract error,
        CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync(DeviceEventNames.ErrorOccurred, error, cancellationToken);
}

internal sealed class NullDeviceEventPublisher : IDeviceEventPublisher
{
    public static readonly NullDeviceEventPublisher Instance = new();

    private NullDeviceEventPublisher() { }

    public Task PublishSnapProgressAsync(
        SnapProgressEventContract progress,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishErrorAsync(
        DeviceErrorEventContract error,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
