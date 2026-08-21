using XRaySystem.Contracts;
using XRaySystem.DeviceHost;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Tests;

public sealed class DeviceCoordinatorTests
{
    [Fact]
    public async Task Coordinator_runs_mock_lifecycle_and_returns_network_safe_snap_metadata()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());

        var initial = coordinator.GetState();
        Assert.Equal(nameof(DeviceStatus.Disconnected), initial.Status);

        var connected = await coordinator.ConnectAsync(Command(), CancellationToken.None);
        Assert.Equal(nameof(DeviceStatus.Connected), connected.Device.Status);

        var parameters = await coordinator.UpdateParametersAsync(
            new UpdateAcquisitionParametersRequest(
                Guid.NewGuid(),
                80,
                2400,
                125,
                nameof(AcquisitionProfile.MedicalSpecimenLikePhantom)),
            CancellationToken.None);
        Assert.Equal(2400, parameters.Device.Parameters.PowerW);
        Assert.Equal(nameof(AcquisitionProfile.MedicalSpecimenLikePhantom), parameters.Device.Profile);

        var live = await coordinator.StartLiveAsync(Command(), CancellationToken.None);
        Assert.Equal(nameof(DeviceStatus.Capturing), live.Device.Status);

        var stopped = await coordinator.StopLiveAsync(Command(), CancellationToken.None);
        Assert.Equal(nameof(DeviceStatus.Connected), stopped.Device.Status);

        var snap = await coordinator.SnapAsync(Command(), CancellationToken.None);
        Assert.True(snap.Succeeded);
        Assert.Equal(2, snap.Image.Width);
        Assert.Equal(2, snap.Image.Height);
        Assert.Equal(nameof(Gray16PixelFormat.Gray16LittleEndian), snap.Image.PixelFormat);
        Assert.Equal(2400, snap.Image.RequestedParameters.PowerW);
        Assert.Equal(80, snap.Image.RequestedParameters.VoltageKv);
        Assert.Equal(2400, snap.Image.MeasuredTelemetry.PowerW);
        Assert.Equal(nameof(AcquisitionProfile.MedicalSpecimenLikePhantom), snap.Image.Profile);
        Assert.True(snap.Image.LowAvailable);
        Assert.True(snap.Image.RawAvailable);
        Assert.DoesNotContain(
            typeof(RemoteImageMetadataContract).GetProperties(),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Pixel", StringComparison.OrdinalIgnoreCase) &&
                        property.PropertyType.IsArray);

        var disconnected = await coordinator.DisconnectAsync(Command(), CancellationToken.None);
        Assert.Equal(nameof(DeviceStatus.Disconnected), disconnected.Device.Status);
        Assert.True(disconnected.Device.SequenceNumber > initial.SequenceNumber);
    }

    [Fact]
    public async Task Coordinator_allows_snap_during_live_and_live_can_restart_from_frozen()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());

        await coordinator.ConnectAsync(Command(), CancellationToken.None);
        await coordinator.StartLiveAsync(Command(), CancellationToken.None);

        var snap = await coordinator.SnapAsync(Command(), CancellationToken.None);

        Assert.Equal(nameof(DeviceStatus.Frozen), snap.Device.Status);
        Assert.Equal(nameof(FrameKind.Snap), snap.Image.Kind);

        var resumed = await coordinator.StartLiveAsync(Command(), CancellationToken.None);
        Assert.Equal(nameof(DeviceStatus.Capturing), resumed.Device.Status);
    }

    [Fact]
    public async Task Duplicate_command_id_returns_cached_result_without_repeating_sensor_operation()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        var command = Command();

        var first = await coordinator.ConnectAsync(command, CancellationToken.None);
        var duplicate = await coordinator.ConnectAsync(command, CancellationToken.None);

        Assert.Equal(1, sensor.ConnectCalls);
        Assert.Same(first, duplicate);
    }

    [Fact]
    public async Task Reusing_command_id_for_a_different_operation_is_rejected()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        var command = Command();

        await coordinator.ConnectAsync(command, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartLiveAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_snap_command_does_not_repeat_acquisition()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        await coordinator.ConnectAsync(Command(), CancellationToken.None);
        var command = Command();

        var first = await coordinator.SnapAsync(command, CancellationToken.None);
        var duplicate = await coordinator.SnapAsync(command, CancellationToken.None);

        Assert.Equal(1, sensor.SnapCalls);
        Assert.Same(first, duplicate);
    }

    [Fact]
    public void Telemetry_event_contains_metadata_but_no_frame_pixels_or_paths()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        var measurement = sensor.CreateMeasurement();

        var contract = coordinator.CreateTelemetryEvent(measurement);

        Assert.Equal("test-device", contract.DeviceId);
        Assert.Equal(2, contract.Width);
        Assert.Equal(3500, contract.RequestedParameters.PowerW);
        Assert.DoesNotContain(
            typeof(TelemetryEventContract).GetProperties(),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                        property.PropertyType.IsArray);
    }

    [Fact]
    public async Task Invalid_parameters_are_rejected_without_changing_the_sensor()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        var original = sensor.Parameters;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            coordinator.UpdateParametersAsync(
                new UpdateAcquisitionParametersRequest(Guid.NewGuid(), 0, 2400, 125),
                CancellationToken.None));

        Assert.Equal(original, sensor.Parameters);
    }

    [Fact]
    public async Task Commands_are_serialized_for_the_single_sensor_owner()
    {
        var sensor = new TestSensor(operationDelay: TimeSpan.FromMilliseconds(30));
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());

        await Task.WhenAll(
            coordinator.ConnectAsync(Command(), CancellationToken.None),
            coordinator.ConnectAsync(Command(), CancellationToken.None));

        Assert.Equal(1, sensor.MaxConcurrentOperations);
    }

    [Fact]
    public async Task Snap_publishes_correlated_monotonic_progress()
    {
        var sensor = new TestSensor();
        await sensor.ConnectAsync();
        var publisher = new TrackingEventPublisher();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer(),
            publisher);
        var command = Command();

        await coordinator.SnapAsync(command, CancellationToken.None);

        Assert.Equal([0, 20, 60, 85, 100], publisher.Progress.Select(item => item.Percent));
        Assert.All(publisher.Progress, item => Assert.Equal(command.CommandId, item.CommandId));
        Assert.Equal("completed", publisher.Progress[^1].Stage);
        Assert.Empty(publisher.Errors);
    }

    [Fact]
    public async Task Failed_command_publishes_safe_correlated_error()
    {
        var publisher = new TrackingEventPublisher();
        using var coordinator = new DeviceCoordinator(
            new TestSensor(),
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer(),
            publisher);
        var command = Command();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartLiveAsync(command, CancellationToken.None));

        var error = Assert.Single(publisher.Errors);
        Assert.Equal(command.CommandId, error.CommandId);
        Assert.Equal("live.start", error.Operation);
        Assert.Equal("invalid_operation", error.Code);
        Assert.DoesNotContain("StackTrace", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceCommandRequest Command() => new(Guid.NewGuid());

    private static RemoteImageBuffer CreateBuffer() =>
        new(new RemoteImageBufferOptions(3, 32L * 1024 * 1024, TimeSpan.FromMinutes(1)));

    private sealed class TrackingEventPublisher : IDeviceEventPublisher
    {
        public List<SnapProgressEventContract> Progress { get; } = [];
        public List<DeviceErrorEventContract> Errors { get; } = [];

        public Task PublishSnapProgressAsync(
            SnapProgressEventContract progress,
            CancellationToken cancellationToken)
        {
            Progress.Add(progress);
            return Task.CompletedTask;
        }

        public Task PublishErrorAsync(
            DeviceErrorEventContract error,
            CancellationToken cancellationToken)
        {
            Errors.Add(error);
            return Task.CompletedTask;
        }
    }

    private sealed class TestSensor(TimeSpan? operationDelay = null) : ISensorService
    {
        private int _activeOperations;
        private int _maxConcurrentOperations;

        public DeviceStatus CurrentStatus { get; private set; } = DeviceStatus.Disconnected;
        public event Action<DeviceStatus>? StatusChanged;
        public event Action<XRayMeasurement>? MeasurementReceived;
        public AcquisitionProfile Profile { get; set; } = AcquisitionProfile.IndustrialQaPhantom;
        public AcquisitionParameters Parameters { get; set; } = new(70, 3500, 500);
        public int MaxConcurrentOperations => Volatile.Read(ref _maxConcurrentOperations);
        public int ConnectCalls { get; private set; }
        public int SnapCalls { get; private set; }

        public Task ConnectAsync() => RunAsync(() =>
        {
            ConnectCalls++;
            SetStatus(DeviceStatus.Connected);
        });

        public Task DisconnectAsync() => RunAsync(() => SetStatus(DeviceStatus.Disconnected));

        public Task StartCaptureAsync() => RunAsync(() =>
        {
            if (CurrentStatus is not (DeviceStatus.Connected or DeviceStatus.Frozen))
                throw new InvalidOperationException("Cihaz bağlı değil.");
            SetStatus(DeviceStatus.Capturing);
        });

        public Task StopCaptureAsync() => RunAsync(() => SetStatus(DeviceStatus.Connected));

        public async Task<XRayMeasurement> SnapAsync(
            double exposureTime,
            CancellationToken cancellationToken = default)
        {
            XRayMeasurement? measurement = null;
            await RunAsync(() =>
            {
                SnapCalls++;
                if (CurrentStatus == DeviceStatus.Disconnected)
                    throw new InvalidOperationException("Cihaz bağlı değil.");
                SetStatus(DeviceStatus.Snapping);
                var settings = new AcquisitionSettings(
                    Parameters.TubeCurrentMa,
                    Parameters.VoltageKv,
                    exposureTime,
                    Profile);
                var frame = new RawGray16Frame(
                    [1, 2, 3, 4],
                    2,
                    2,
                    4,
                    Gray16PixelFormat.Gray16LittleEndian,
                    FrameKind.Snap,
                    DateTimeOffset.UtcNow,
                    Guid.NewGuid(),
                    settings,
                    "test-sensor");
                measurement = new XRayMeasurement(
                    new SensorReadings(
                        Parameters.TubeCurrentMa,
                        Parameters.VoltageKv,
                        0.5,
                        exposureTime),
                    frame,
                    isSimulator: true);
                SetStatus(DeviceStatus.Frozen);
                MeasurementReceived?.Invoke(measurement);
            }, cancellationToken);
            return measurement!;
        }

        public Task ResumeLiveAsync() => StartCaptureAsync();

        public XRayMeasurement CreateMeasurement()
        {
            var settings = new AcquisitionSettings(
                Parameters.TubeCurrentMa,
                Parameters.VoltageKv,
                Parameters.ExposureTimeMs,
                Profile);
            var frame = new RawGray16Frame(
                [1, 2, 3, 4],
                2,
                2,
                4,
                Gray16PixelFormat.Gray16LittleEndian,
                FrameKind.LivePreview,
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                settings,
                "test-sensor");
            return new XRayMeasurement(
                new SensorReadings(
                    Parameters.TubeCurrentMa,
                    Parameters.VoltageKv,
                    0.5,
                    Parameters.ExposureTimeMs),
                frame,
                isSimulator: true);
        }

        private async Task RunAsync(Action operation, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeOperations);
            UpdateMax(active);
            try
            {
                if (operationDelay is { } delay)
                    await Task.Delay(delay, cancellationToken);
                operation();
            }
            finally
            {
                Interlocked.Decrement(ref _activeOperations);
            }
        }

        private void UpdateMax(int active)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrentOperations);
                if (observed >= active) return;
            }
            while (Interlocked.CompareExchange(ref _maxConcurrentOperations, active, observed) != observed);
        }

        private void SetStatus(DeviceStatus status)
        {
            CurrentStatus = status;
            StatusChanged?.Invoke(status);
        }
    }
}
