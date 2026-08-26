using XRaySystem.Contracts;
using XRaySystem.DeviceHost;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using XRaySystem.Infrastructure.Storage;

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
        Assert.Equal(nameof(DeviceStatus.Grabbing), live.Device.Status);

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
        Assert.Equal(AcquisitionOperationContract.Snap, snap.Image.Operation);
        Assert.Equal(SnapFrameModeContract.Raw, snap.Image.SnapFrameMode);
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
        Assert.Equal(nameof(DeviceStatus.Grabbing), resumed.Device.Status);
    }

    [Fact]
    public async Task Coordinator_routes_snap_low_and_raw_as_distinct_idempotent_operations()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        await coordinator.ConnectAsync(Command(), CancellationToken.None);
        var commandId = Guid.NewGuid();

        var low = await coordinator.SnapAsync(
            new SnapCommandRequest(commandId, SnapFrameModeContract.Low),
            CancellationToken.None);

        Assert.Equal(SnapFrameMode.Low, sensor.LastSnapMode);
        Assert.Equal(SnapFrameModeContract.Low, low.Image.SnapFrameMode);
        Assert.Equal(AcquisitionOperationContract.Snap, low.Image.Operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.SnapAsync(
                new SnapCommandRequest(commandId, SnapFrameModeContract.Raw),
                CancellationToken.None));
        Assert.Equal(1, sensor.SnapCalls);
    }

    [Fact]
    public async Task Grab_and_freeze_use_the_new_server_operations()
    {
        var sensor = new TestSensor();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        await coordinator.ConnectAsync(Command(), CancellationToken.None);

        var grabbing = await coordinator.StartGrabAsync(Command(), CancellationToken.None);
        var frozen = await coordinator.FreezeGrabAsync(Command(), CancellationToken.None);

        Assert.Equal(nameof(DeviceStatus.Grabbing), grabbing.Device.Status);
        Assert.Equal(nameof(DeviceStatus.Connected), frozen.Device.Status);
    }

    [Fact]
    public async Task Device_state_exposes_verified_generator_and_detector_flags_for_coordinated_hardware()
    {
        using var sensor = new XRaySystem.Infrastructure.Sensors.CoordinatedHardwareSensorService(
            new XRaySystem.Infrastructure.Sensors.MockXRayGenerator(),
            new XRaySystem.Infrastructure.Sensors.MockXRayDetector());
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());

        var connected = await coordinator.ConnectAsync(Command(), CancellationToken.None);
        Assert.Equal(nameof(AcquisitionCoordinatorState.Idle), connected.Device.CoordinatorState);
        Assert.True(connected.Device.GeneratorConnected);
        Assert.True(connected.Device.DetectorConnected);
        Assert.False(connected.Device.XRayActive);
        Assert.False(connected.Device.DetectorAcquiring);

        var grabbing = await coordinator.StartGrabAsync(Command(), CancellationToken.None);
        Assert.Equal(nameof(AcquisitionCoordinatorState.Grabbing), grabbing.Device.CoordinatorState);
        Assert.True(grabbing.Device.XRayActive);
        Assert.True(grabbing.Device.DetectorAcquiring);
        Assert.NotNull(grabbing.Device.ActiveAcquisitionId);

        var frozen = await coordinator.FreezeGrabAsync(Command(), CancellationToken.None);
        Assert.Equal(nameof(AcquisitionCoordinatorState.Frozen), frozen.Device.CoordinatorState);
        Assert.False(frozen.Device.XRayActive);
        Assert.False(frozen.Device.DetectorAcquiring);
        Assert.Null(frozen.Device.ActiveAcquisitionId);
    }

    [Fact]
    public async Task Abort_preempts_an_active_snap_without_waiting_for_the_command_queue()
    {
        using var sensor = new XRaySystem.Infrastructure.Sensors.MockXRaySensorService
        {
            Parameters = new AcquisitionParameters(70, 3500, 2000)
        };
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        await coordinator.ConnectAsync(Command(), CancellationToken.None);
        var snapTask = coordinator.SnapAsync(
            new SnapCommandRequest(Guid.NewGuid(), SnapFrameModeContract.Raw),
            CancellationToken.None);
        for (var attempt = 0; attempt < 100 && sensor.CurrentStatus != DeviceStatus.Snapping; attempt++)
            await Task.Delay(10);
        Assert.Equal(DeviceStatus.Snapping, sensor.CurrentStatus);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var aborted = await coordinator.AbortAsync(Command(), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapTask);
        stopwatch.Stop();

        Assert.True(aborted.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(nameof(DeviceStatus.Connected), aborted.Device.Status);
    }

    [Fact]
    public async Task Disconnect_safe_stops_an_active_snap_before_releasing_the_device()
    {
        using var sensor = new XRaySystem.Infrastructure.Sensors.MockXRaySensorService
        {
            Parameters = new AcquisitionParameters(70, 3500, 2000)
        };
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            CreateBuffer());
        await coordinator.ConnectAsync(Command(), CancellationToken.None);
        var snapTask = coordinator.SnapAsync(
            new SnapCommandRequest(Guid.NewGuid(), SnapFrameModeContract.Raw),
            CancellationToken.None);
        for (var attempt = 0; attempt < 100 && sensor.CurrentStatus != DeviceStatus.Snapping; attempt++)
            await Task.Delay(10);
        Assert.Equal(DeviceStatus.Snapping, sensor.CurrentStatus);

        var disconnected = await coordinator.DisconnectAsync(Command(), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapTask);

        Assert.Equal(nameof(DeviceStatus.Disconnected), disconnected.Device.Status);
        Assert.Equal(DeviceStatus.Disconnected, sensor.CurrentStatus);
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

        var snap = await coordinator.SnapAsync(command, CancellationToken.None);

        Assert.Equal([0, 20, 60, 75, 85, 90, 100], publisher.Progress.Select(item => item.Percent));
        Assert.All(publisher.Progress, item => Assert.Equal(command.CommandId, item.CommandId));
        Assert.Equal("completed", publisher.Progress[^1].Stage);
        var frameReady = Assert.Single(publisher.Frames);
        Assert.Equal(snap.Image.ImageId, frameReady.ImageId);
        Assert.Equal(snap.Image.AcquisitionId, frameReady.AcquisitionId);
        Assert.Empty(publisher.Errors);
    }

    [Fact]
    public async Task Snap_persists_verified_raw_and_preview_on_the_device_before_success()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xray-device-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sensor = new TestSensor();
            var buffer = CreateBuffer();
            var archive = new DeviceAcquisitionArchive(
                new DeviceAcquisitionArchiveOptions(
                    true,
                    folder,
                    RequireSignedMetadata: true,
                    IntegrityKey: "device-coordinator-integrity-test-key-0001"),
                NullLogger<DeviceAcquisitionArchive>.Instance);
            using var coordinator = new DeviceCoordinator(
                sensor,
                new DeviceHostOptions("test-device", IsSimulator: true),
                buffer,
                acquisitionArchive: archive);
            await coordinator.ConnectAsync(Command(), CancellationToken.None);

            var snap = await coordinator.SnapAsync(Command(), CancellationToken.None);

            Assert.True(snap.Image.PersistedOnDevice);
            Assert.Equal(1, buffer.Count);
            var rawPath = Assert.Single(Directory.GetFiles(folder, "*.xrayraw"));
            Assert.Single(Directory.GetFiles(folder, "*.bmp"));
            Assert.Single(Directory.GetFiles(folder, "*.xraymeta.json"));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
            using var stream = File.OpenRead(rawPath);
            var restored = Gray16RawFileCodec.Read(stream);
            Assert.Equal(snap.Image.AcquisitionId, restored.AcquisitionId);

            var recovered = Assert.Single(archive.LoadRecent(10));
            Assert.Equal(snap.Image.ImageId, recovered.Metadata.ImageId);
            Assert.Equal(snap.Image.AcquisitionId, recovered.Frame.AcquisitionId);
            Assert.Equal(snap.Image.MeasuredTelemetry, recovered.Metadata.MeasuredTelemetry);
            Assert.True(recovered.Metadata.PersistedOnDevice);

            var recoveredOptions = new RemoteImageBufferOptions(
                ImageLimit: 3,
                MaxBytes: 32L * 1024 * 1024,
                TimeToLive: TimeSpan.FromMinutes(1));
            var recoveredBuffer = new RemoteImageBuffer(recoveredOptions);
            var recoveryService = new DeviceArchiveRecoveryService(
                archive,
                recoveredBuffer,
                recoveredOptions,
                NullLogger<DeviceArchiveRecoveryService>.Instance);
            await recoveryService.StartAsync(CancellationToken.None);
            Assert.Equal(snap.Image.ImageId, Assert.Single(recoveredBuffer.GetMetadata()).ImageId);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Durable_catalog_keeps_all_verified_snapshots_after_transfer_buffer_eviction()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xray-device-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new DeviceAcquisitionArchive(
                new DeviceAcquisitionArchiveOptions(true, folder),
                NullLogger<DeviceAcquisitionArchive>.Instance);
            var buffer = new RemoteImageBuffer(new RemoteImageBufferOptions(
                ImageLimit: 1,
                MaxBytes: 32L * 1024 * 1024,
                TimeToLive: null));
            using var coordinator = new DeviceCoordinator(
                new TestSensor(),
                new DeviceHostOptions("test-device", IsSimulator: true),
                buffer,
                acquisitionArchive: archive);
            await coordinator.ConnectAsync(Command(), CancellationToken.None);

            var first = await coordinator.SnapAsync(Command(), CancellationToken.None);
            var second = await coordinator.SnapAsync(Command(), CancellationToken.None);

            Assert.Equal(second.Image.ImageId, Assert.Single(buffer.GetMetadata()).ImageId);
            var catalog = archive.GetMetadataPage(skip: 0, take: 10);
            Assert.Equal(2, catalog.Count);
            Assert.Contains(catalog, image => image.ImageId == first.Image.ImageId);
            Assert.Contains(catalog, image => image.ImageId == second.Image.ImageId);
            Assert.True(archive.TryLoad(first.Image.ImageId, out var restored));
            Assert.NotNull(restored);
            Assert.Equal(first.Image.AcquisitionId, restored.Frame.AcquisitionId);

            var restartedArchive = new DeviceAcquisitionArchive(
                new DeviceAcquisitionArchiveOptions(true, folder),
                NullLogger<DeviceAcquisitionArchive>.Instance);
            var rebuiltCatalog = restartedArchive.GetMetadataPage(skip: 0, take: 10);
            Assert.Equal(2, rebuiltCatalog.Count);
            Assert.Contains(rebuiltCatalog, image => image.ImageId == first.Image.ImageId);
            Assert.Contains(rebuiltCatalog, image => image.ImageId == second.Image.ImageId);
            Assert.True(restartedArchive.TryLoad(first.Image.ImageId, out var restartedImage));
            Assert.Equal(first.Image.AcquisitionId, restartedImage!.Frame.AcquisitionId);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Archive_recovery_ignores_a_preview_that_no_longer_matches_its_manifest_hash()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xray-device-archive-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sensor = new TestSensor();
            var archive = new DeviceAcquisitionArchive(
                new DeviceAcquisitionArchiveOptions(true, folder),
                NullLogger<DeviceAcquisitionArchive>.Instance);
            using var coordinator = new DeviceCoordinator(
                sensor,
                new DeviceHostOptions("test-device", IsSimulator: true),
                CreateBuffer(),
                acquisitionArchive: archive);
            await coordinator.ConnectAsync(Command(), CancellationToken.None);
            await coordinator.SnapAsync(Command(), CancellationToken.None);
            var previewPath = Assert.Single(Directory.GetFiles(folder, "*.bmp"));
            var tampered = File.ReadAllBytes(previewPath);
            tampered[^1] ^= 0xFF;
            File.WriteAllBytes(previewPath, tampered);

            Assert.Empty(archive.LoadRecent(10));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Signed_archive_recovery_rejects_valid_json_when_metadata_was_tampered()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xray-device-archive-signed-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string integrityKey = "signed-archive-integrity-test-key-000001";
            var sensor = new TestSensor();
            var archive = new DeviceAcquisitionArchive(
                new DeviceAcquisitionArchiveOptions(
                    true,
                    folder,
                    RequireSignedMetadata: true,
                    IntegrityKey: integrityKey),
                NullLogger<DeviceAcquisitionArchive>.Instance);
            using var coordinator = new DeviceCoordinator(
                sensor,
                new DeviceHostOptions("test-device", IsSimulator: true),
                CreateBuffer(),
                acquisitionArchive: archive);
            await coordinator.ConnectAsync(Command(), CancellationToken.None);
            await coordinator.SnapAsync(Command(), CancellationToken.None);
            var manifestPath = Assert.Single(Directory.GetFiles(folder, "*.xraymeta.json"));
            var json = File.ReadAllText(manifestPath);
            Assert.Contains("integrityHmacSha256", json, StringComparison.Ordinal);
            var tamperedJson = json.Replace("\"powerW\":3500", "\"powerW\":3501");
            Assert.NotEqual(json, tamperedJson);
            File.WriteAllText(manifestPath, tamperedJson);

            Assert.Empty(archive.LoadRecent(10));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Signed_archive_requires_a_strong_integrity_key()
    {
        var missing = new DeviceAcquisitionArchiveOptions(
            true,
            Path.Combine(Path.GetTempPath(), "xray-unused-archive"),
            RequireSignedMetadata: true);
        var weak = missing with { IntegrityKey = "too-short" };

        Assert.Throws<ArgumentException>(missing.EnsureValid);
        Assert.Throws<ArgumentException>(weak.EnsureValid);
    }

    [Fact]
    public async Task Persistence_failure_is_not_reported_as_success_and_retry_does_not_repeat_exposure()
    {
        var sensor = new TestSensor();
        var buffer = CreateBuffer();
        var publisher = new TrackingEventPublisher();
        using var coordinator = new DeviceCoordinator(
            sensor,
            new DeviceHostOptions("test-device", IsSimulator: true),
            buffer,
            publisher,
            new FailingArchive());
        await coordinator.ConnectAsync(Command(), CancellationToken.None);
        var command = Command();

        await Assert.ThrowsAsync<AcquisitionPersistenceException>(() =>
            coordinator.SnapAsync(command, CancellationToken.None));
        await Assert.ThrowsAsync<AcquisitionPersistenceException>(() =>
            coordinator.SnapAsync(command, CancellationToken.None));

        Assert.Equal(1, sensor.SnapCalls);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(2, publisher.Errors.Count(error => error.Code == "persistence_failed"));
        Assert.DoesNotContain(publisher.Progress, progress => progress.Stage == "completed");
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
        public List<FrameReadyEventContract> Frames { get; } = [];
        public List<DeviceErrorEventContract> Errors { get; } = [];

        public Task PublishSnapProgressAsync(
            SnapProgressEventContract progress,
            CancellationToken cancellationToken)
        {
            Progress.Add(progress);
            return Task.CompletedTask;
        }

        public Task PublishFrameReadyAsync(
            FrameReadyEventContract frame,
            CancellationToken cancellationToken)
        {
            Frames.Add(frame);
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

    private sealed class FailingArchive : IDeviceAcquisitionArchive
    {
        public DeviceAcquisitionArchiveReceipt Save(
            XRayMeasurement measurement,
            RemoteImageMetadataContract metadata,
            byte[] lowPreviewBmp) =>
            throw new AcquisitionPersistenceException(
                "Test persistence failure.",
                new IOException("Disk unavailable."));

        public IReadOnlyList<ArchivedRemoteImage> LoadRecent(int maximumCount) => [];
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
        public SnapFrameMode LastSnapMode { get; private set; } = SnapFrameMode.Raw;

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

        public Task<XRayMeasurement> SnapAsync(
            double exposureTime,
            CancellationToken cancellationToken = default) =>
            SnapAsync(new SnapRequest(exposureTime, SnapFrameMode.Raw), cancellationToken);

        public async Task<XRayMeasurement> SnapAsync(
            SnapRequest request,
            CancellationToken cancellationToken = default)
        {
            XRayMeasurement? measurement = null;
            await RunAsync(() =>
            {
                SnapCalls++;
                LastSnapMode = request.FrameMode;
                if (CurrentStatus == DeviceStatus.Disconnected)
                    throw new InvalidOperationException("Cihaz bağlı değil.");
                SetStatus(DeviceStatus.Snapping);
                var settings = new AcquisitionSettings(
                    Parameters.TubeCurrentMa,
                    Parameters.VoltageKv,
                    request.ExposureTimeMs,
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
                    "test-sensor",
                    operation: AcquisitionOperation.Snap,
                    snapFrameMode: request.FrameMode);
                measurement = new XRayMeasurement(
                    new SensorReadings(
                        Parameters.TubeCurrentMa,
                        Parameters.VoltageKv,
                        0.5,
                        request.ExposureTimeMs),
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
