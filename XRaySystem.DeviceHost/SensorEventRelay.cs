using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using XRaySystem.Contracts;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Domain.WindowLevel;
using XRaySystem.Infrastructure.Imaging;

namespace XRaySystem.DeviceHost;

/// <summary>
/// Relays sensor status and metadata-only telemetry to SignalR. The measurement
/// channel has capacity one so slow clients never create an unbounded frame queue.
/// </summary>
public sealed class SensorEventRelay : IHostedService, IDisposable
{
    private readonly ISensorService _sensor;
    private readonly DeviceCoordinator _coordinator;
    private readonly IHubContext<DeviceEventsHub> _hub;
    private readonly LatestLiveFrameStore _latestLiveFrame;
    private readonly Channel<XRayMeasurement> _measurements = Channel.CreateBounded<XRayMeasurement>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly Channel<DeviceStateContract> _statuses = Channel.CreateBounded<DeviceStateContract>(
        new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly CancellationTokenSource _shutdown = new();
    private Task _pumpTask = Task.CompletedTask;
    private bool _started;

    public SensorEventRelay(
        ISensorService sensor,
        DeviceCoordinator coordinator,
        IHubContext<DeviceEventsHub> hub,
        LatestLiveFrameStore latestLiveFrame)
    {
        _sensor = sensor;
        _coordinator = coordinator;
        _hub = hub;
        _latestLiveFrame = latestLiveFrame;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        _sensor.StatusChanged += OnStatusChanged;
        _sensor.MeasurementReceived += OnMeasurementReceived;
        _pumpTask = Task.WhenAll(
            PumpStatusesAsync(_shutdown.Token),
            PumpMeasurementsAsync(_shutdown.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started) return;
        _sensor.StatusChanged -= OnStatusChanged;
        _sensor.MeasurementReceived -= OnMeasurementReceived;
        _measurements.Writer.TryComplete();
        _statuses.Writer.TryComplete();
        _shutdown.Cancel();
        try { await _pumpTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _started = false;
    }

    private void OnStatusChanged(DeviceStatus status)
    {
        if (status != DeviceStatus.Capturing) _latestLiveFrame.Clear();
        _statuses.Writer.TryWrite(_coordinator.GetState());
    }

    private void OnMeasurementReceived(XRayMeasurement measurement) =>
        _measurements.Writer.TryWrite(measurement);

    private async Task PumpStatusesAsync(CancellationToken cancellationToken)
    {
        await foreach (var state in _statuses.Reader.ReadAllAsync(cancellationToken))
        {
            await _hub.Clients.All.SendAsync(
                DeviceEventNames.StatusChanged,
                new DeviceStatusEventContract(state),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpMeasurementsAsync(CancellationToken cancellationToken)
    {
        await foreach (var measurement in _measurements.Reader.ReadAllAsync(cancellationToken))
        {
            var telemetry = _coordinator.CreateTelemetryEvent(measurement);
            if (measurement.Frame is { Kind: FrameKind.LivePreview } frame)
            {
                var autoWindow = WindowLevelMapper.Auto(frame.Pixels);
                var preview = Gray8BmpEncoder.EncodePreview(
                    frame,
                    maxWidth: 768,
                    maxHeight: 768,
                    autoWindow.Center,
                    autoWindow.Width,
                    cancellationToken);
                telemetry = telemetry with
                {
                    LowFrameAvailable = true,
                    LowWidth = preview.Width,
                    LowHeight = preview.Height
                };
                _latestLiveFrame.Set(telemetry, preview.Bytes);
            }
            await _hub.Clients.All.SendAsync(
                DeviceEventNames.TelemetryReceived,
                telemetry,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _sensor.StatusChanged -= OnStatusChanged;
        _sensor.MeasurementReceived -= OnMeasurementReceived;
        _shutdown.Dispose();
    }
}
