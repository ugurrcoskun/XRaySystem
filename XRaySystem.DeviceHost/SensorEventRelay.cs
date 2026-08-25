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
    private DeviceStateContract? _lastPublishedState;
    private Guid? _lastGrabAcquisitionId;

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
        if (_sensor is IAcquisitionCoordinator coordinator)
            coordinator.HardwareStateChanged += OnHardwareStateChanged;
        _sensor.MeasurementReceived += OnMeasurementReceived;
        _pumpTask = Task.WhenAll(
            PumpStatusesAsync(_shutdown.Token),
            PumpMeasurementsAsync(_shutdown.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started) return;
        try
        {
            await _sensor.AbortAsync()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch
        {
            // Shutdown continues; the acquisition coordinator also has bounded stop timeouts.
        }
        _sensor.StatusChanged -= OnStatusChanged;
        if (_sensor is IAcquisitionCoordinator coordinator)
            coordinator.HardwareStateChanged -= OnHardwareStateChanged;
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
        if (status != DeviceStatus.Grabbing) _latestLiveFrame.Clear();
        _statuses.Writer.TryWrite(_coordinator.GetState());
    }

    private void OnHardwareStateChanged(AcquisitionHardwareState _) =>
        _statuses.Writer.TryWrite(_coordinator.GetState());

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
            await _hub.Clients.All.SendAsync(
                DeviceEventNames.HardwareStateChanged,
                new HardwareStateEventContract(
                    state.DeviceId,
                    state.SequenceNumber,
                    state.CoordinatorState,
                    state.GeneratorConnected,
                    state.DetectorConnected,
                    state.XRayActive,
                    state.DetectorAcquiring,
                    state.ActiveAcquisitionId,
                    state.TimestampUtc),
                cancellationToken).ConfigureAwait(false);

            if (_lastPublishedState?.XRayActive == true && state.XRayActive == false)
            {
                await _hub.Clients.All.SendAsync(
                    DeviceEventNames.ExposureEnded,
                    new ExposureEndedEventContract(
                        state.DeviceId,
                        state.SequenceNumber,
                        _lastPublishedState.ActiveAcquisitionId,
                        state.TimestampUtc),
                    cancellationToken).ConfigureAwait(false);
            }
            if (!string.Equals(
                    _lastPublishedState?.CoordinatorState,
                    nameof(AcquisitionCoordinatorState.Frozen),
                    StringComparison.Ordinal) &&
                string.Equals(
                    state.CoordinatorState,
                    nameof(AcquisitionCoordinatorState.Frozen),
                    StringComparison.Ordinal))
            {
                await _hub.Clients.All.SendAsync(
                    DeviceEventNames.GrabFrozen,
                    new GrabFrozenEventContract(
                        state.DeviceId,
                        state.SequenceNumber,
                        _lastGrabAcquisitionId,
                        state.TimestampUtc),
                    cancellationToken).ConfigureAwait(false);
            }
            _lastPublishedState = state;
        }
    }

    private async Task PumpMeasurementsAsync(CancellationToken cancellationToken)
    {
        await foreach (var measurement in _measurements.Reader.ReadAllAsync(cancellationToken))
        {
            var telemetry = _coordinator.CreateTelemetryEvent(measurement);
            if (measurement.Frame is { Kind: FrameKind.GrabPreview } frame)
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
                _lastGrabAcquisitionId = frame.AcquisitionId;
                await _hub.Clients.All.SendAsync(
                    DeviceEventNames.GrabFrameReady,
                    new GrabFrameReadyEventContract(
                        telemetry.DeviceId,
                        telemetry.SequenceNumber,
                        frame.AcquisitionId,
                        preview.Width,
                        preview.Height,
                        telemetry.CapturedAtUtc),
                    cancellationToken).ConfigureAwait(false);
            }
            await _hub.Clients.All.SendAsync(
                DeviceEventNames.FrameReady,
                new FrameReadyEventContract(
                    telemetry.DeviceId,
                    telemetry.SequenceNumber,
                    telemetry.AcquisitionId,
                    telemetry.Operation,
                    telemetry.FrameKind,
                    telemetry.Width,
                    telemetry.Height,
                    telemetry.CapturedAtUtc),
                cancellationToken).ConfigureAwait(false);
            await _hub.Clients.All.SendAsync(
                DeviceEventNames.TelemetryReceived,
                telemetry,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _sensor.StatusChanged -= OnStatusChanged;
        if (_sensor is IAcquisitionCoordinator coordinator)
            coordinator.HardwareStateChanged -= OnHardwareStateChanged;
        _sensor.MeasurementReceived -= OnMeasurementReceived;
        _shutdown.Dispose();
    }
}
