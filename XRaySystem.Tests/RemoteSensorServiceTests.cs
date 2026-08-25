using XRaySystem.Contracts;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Imaging;
using XRaySystem.Infrastructure.Storage;
using XRaySystem.RemoteClient;

namespace XRaySystem.Tests;

public sealed class RemoteSensorServiceTests
{
    [Fact]
    public async Task Adapter_maps_lifecycle_parameters_profile_and_raw_snap()
    {
        var fake = new FakeRemoteDeviceClient();
        using var service = new RemoteSensorService(fake);
        var statuses = new List<DeviceStatus>();
        service.StatusChanged += statuses.Add;

        await service.ConnectAsync();
        service.Parameters = new AcquisitionParameters(80, 2400, 25);
        service.Profile = AcquisitionProfile.MedicalSpecimenLikePhantom;
        await service.StartCaptureAsync();
        var measurement = await service.SnapAsync(25);

        Assert.True(fake.EventsConnected);
        Assert.Equal(80, fake.LastParameters!.VoltageKv);
        Assert.Equal(2400, fake.LastParameters.PowerW);
        Assert.Equal(nameof(AcquisitionProfile.MedicalSpecimenLikePhantom), fake.LastProfile);
        Assert.Equal(fake.ImageId, measurement.Id);
        Assert.Equal(fake.AcquisitionId, measurement.Frame!.AcquisitionId);
        Assert.Equal(FrameKind.Snap, measurement.Frame.Kind);
        Assert.Equal(80, measurement.RequestedParameters.VoltageKv);
        Assert.Equal(2400, measurement.RequestedParameters.PowerW);
        Assert.Equal(MeasurementProvenance.Measured, measurement.Readings.VoltageProvenance);
        Assert.Equal(MeasurementProvenance.Derived, measurement.Readings.TubeCurrentProvenance);
        Assert.Equal(DeviceStatus.Frozen, service.CurrentStatus);
        Assert.Contains(DeviceStatus.Capturing, statuses);

        await service.ResumeLiveAsync();
        Assert.Equal(DeviceStatus.Capturing, service.CurrentStatus);

        await service.DisconnectAsync();
        Assert.False(fake.EventsConnected);
        Assert.Equal(DeviceStatus.Disconnected, service.CurrentStatus);
    }

    [Fact]
    public async Task Adapter_converts_remote_low_bmp_to_canonical_live_frame()
    {
        var fake = new FakeRemoteDeviceClient();
        using var service = new RemoteSensorService(fake);
        await service.ConnectAsync();
        XRayMeasurement? received = null;
        service.MeasurementReceived += measurement => received = measurement;
        var telemetry = fake.CreateTelemetry(sequence: 7);
        var source = fake.CreateFrame(FrameKind.LivePreview, telemetry.AcquisitionId);
        var bmp = Gray8BmpEncoder.Encode(source, 32767.5, 65535);

        fake.RaiseTelemetry(telemetry);
        fake.RaiseLowFrame(new RemoteLowFrame(
            telemetry,
            telemetry.SequenceNumber,
            telemetry.AcquisitionId,
            source.Width,
            source.Height,
            bmp));

        Assert.NotNull(received);
        Assert.Equal(FrameKind.LivePreview, received.Frame!.Kind);
        Assert.Equal(telemetry.AcquisitionId, received.Frame.AcquisitionId);
        Assert.Equal("remote-grab-low-bmp:7", received.Frame.SourceMarker);
        Assert.Equal(source.Width * source.Height, received.Frame.Pixels.Length);
        Assert.Equal(DeviceStatus.Connected, service.CurrentStatus);
    }

    [Fact]
    public async Task Adapter_exposes_xray_active_only_from_verified_remote_hardware_events()
    {
        var fake = new FakeRemoteDeviceClient();
        using var service = new RemoteSensorService(fake);
        var observed = new List<AcquisitionHardwareState>();
        service.HardwareStateChanged += observed.Add;
        await service.ConnectAsync();

        fake.RaiseHardwareState(new HardwareStateEventContract(
            "mock-01",
            2,
            nameof(AcquisitionCoordinatorState.Exposing),
            GeneratorConnected: true,
            DetectorConnected: true,
            XRayActive: true,
            DetectorAcquiring: true,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow));

        Assert.True(service.HardwareState.XRayActive);
        Assert.True(service.HardwareState.IsVerified);
        Assert.NotNull(service.HardwareState.VerifiedAtUtc);
        Assert.Equal(AcquisitionCoordinatorState.Exposing, service.CoordinatorState);
        Assert.True(Assert.Single(observed).XRayActive);
    }

    [Fact]
    public async Task Connection_loss_invalidates_last_verified_hardware_state()
    {
        var fake = new FakeRemoteDeviceClient();
        using var service = new RemoteSensorService(fake);
        var observed = new List<AcquisitionHardwareState>();
        service.HardwareStateChanged += observed.Add;
        await service.ConnectAsync();

        fake.RaiseHardwareState(new HardwareStateEventContract(
            "mock-01",
            2,
            nameof(AcquisitionCoordinatorState.Exposing),
            GeneratorConnected: true,
            DetectorConnected: true,
            XRayActive: true,
            DetectorAcquiring: true,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow));
        Assert.True(service.HardwareState.IsVerified);
        Assert.True(service.HardwareState.XRayActive);

        fake.RaiseConnectionState(connected: false);

        Assert.Equal(DeviceStatus.Error, service.CurrentStatus);
        Assert.False(service.HardwareState.IsVerified);
        Assert.False(service.HardwareState.XRayActive);
        Assert.False(service.HardwareState.DetectorAcquiring);
        Assert.Null(service.HardwareState.ActiveAcquisitionId);
        Assert.Null(service.HardwareState.VerifiedAtUtc);
        Assert.Equal(AcquisitionCoordinatorState.Faulted, service.CoordinatorState);
        Assert.False(observed[^1].IsVerified);
    }

    [Fact]
    public async Task Reconnect_accepts_fresh_state_after_remote_sequence_restart()
    {
        var fake = new FakeRemoteDeviceClient();
        using var service = new RemoteSensorService(fake);
        await service.ConnectAsync();
        fake.RaiseHardwareState(new HardwareStateEventContract(
            "mock-01",
            50,
            nameof(AcquisitionCoordinatorState.Exposing),
            true,
            true,
            true,
            true,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow));
        Assert.True(service.HardwareState.XRayActive);

        fake.RaiseConnectionState(connected: false);
        fake.SetReconnectState(sequence: 0);
        var refreshed = new TaskCompletionSource<AcquisitionHardwareState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.HardwareStateChanged += state =>
        {
            if (state.IsVerified && state.CoordinatorState == AcquisitionCoordinatorState.Idle)
                refreshed.TrySetResult(state);
        };

        fake.RaiseConnectionState(connected: true);
        var state = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(state.IsVerified);
        Assert.False(state.XRayActive);
        Assert.Equal(AcquisitionCoordinatorState.Idle, service.CoordinatorState);
    }

    [Fact]
    public async Task Delayed_status_event_cannot_overwrite_new_local_targets_or_reactivate_stale_hardware()
    {
        var fake = new FakeRemoteDeviceClient();
        using var service = new RemoteSensorService(fake);
        await service.ConnectAsync();
        service.Parameters = new AcquisitionParameters(82, 2460, 15);
        service.Profile = AcquisitionProfile.MedicalSpecimenLikePhantom;

        fake.RaiseStatus(DeviceStatus.Connected, sequence: 1);
        fake.RaiseHardwareState(new HardwareStateEventContract(
            "mock-01",
            0,
            nameof(AcquisitionCoordinatorState.Exposing),
            true,
            true,
            true,
            true,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow));

        Assert.Equal(82, service.Parameters.VoltageKv);
        Assert.Equal(2460, service.Parameters.PowerW);
        Assert.Equal(AcquisitionProfile.MedicalSpecimenLikePhantom, service.Profile);
        Assert.False(service.HardwareState.XRayActive);
    }

    [Fact]
    public async Task Delayed_status_event_cannot_overwrite_newer_verified_hardware_event()
    {
        var fake = new FakeRemoteDeviceClient();
        using var service = new RemoteSensorService(fake);
        await service.ConnectAsync();

        fake.RaiseHardwareState(new HardwareStateEventContract(
            "mock-01",
            10,
            nameof(AcquisitionCoordinatorState.Exposing),
            true,
            true,
            true,
            true,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow));
        fake.RaiseStatus(DeviceStatus.Connected, sequence: 2);

        Assert.True(service.HardwareState.IsVerified);
        Assert.True(service.HardwareState.XRayActive);
        Assert.Equal(AcquisitionCoordinatorState.Exposing, service.CoordinatorState);
    }

    private sealed class FakeRemoteDeviceClient : IRemoteDeviceClient
    {
        private DeviceStateContract _state;

        public FakeRemoteDeviceClient()
        {
            ImageId = Guid.NewGuid();
            AcquisitionId = Guid.NewGuid();
            _state = State(DeviceStatus.Disconnected);
        }

        public Guid ImageId { get; }
        public Guid AcquisitionId { get; }
        public AcquisitionParametersContract? LastParameters { get; private set; }
        public string? LastProfile { get; private set; }

        public event Action<DeviceStateContract>? StatusChanged;
        public event Action<TelemetryEventContract>? TelemetryReceived;
        public event Action<RemoteLowFrame>? LowFrameReceived;
        public event Action<SnapProgressEventContract>? SnapProgressReceived
        {
            add { }
            remove { }
        }
        public event Action<HardwareStateEventContract>? HardwareStateReceived;
        public event Action<FrameReadyEventContract>? FrameReadyReceived
        {
            add { }
            remove { }
        }
        public event Action<GrabFrameReadyEventContract>? GrabFrameReadyReceived
        {
            add { }
            remove { }
        }
        public event Action<GrabFrozenEventContract>? GrabFrozenReceived
        {
            add { }
            remove { }
        }
        public event Action<ExposureEndedEventContract>? ExposureEndedReceived
        {
            add { }
            remove { }
        }
        public event Action<DeviceErrorEventContract>? ErrorReceived
        {
            add { }
            remove { }
        }
        public event Action<bool>? ConnectionStateChanged;

        public bool EventsConnected { get; private set; }

        public Task ConnectEventsAsync(CancellationToken cancellationToken = default)
        {
            EventsConnected = true;
            ConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        }

        public Task DisconnectEventsAsync(CancellationToken cancellationToken = default)
        {
            EventsConnected = false;
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        }

        public Task<DeviceStateContract> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_state);

        public Task<DeviceCommandResult> ConnectDeviceAsync(CancellationToken cancellationToken = default) =>
            Command(DeviceStatus.Connected, "connected");

        public Task<DeviceCommandResult> DisconnectDeviceAsync(CancellationToken cancellationToken = default) =>
            Command(DeviceStatus.Disconnected, "disconnected");

        public Task<DeviceCommandResult> StartLiveAsync(CancellationToken cancellationToken = default) =>
            Command(DeviceStatus.Capturing, "started");

        public Task<DeviceCommandResult> StopLiveAsync(CancellationToken cancellationToken = default) =>
            Command(DeviceStatus.Connected, "stopped");

        public Task<DeviceCommandResult> UpdateParametersAsync(
            AcquisitionParametersContract parameters,
            string profile,
            CancellationToken cancellationToken = default)
        {
            LastParameters = parameters;
            LastProfile = profile;
            _state = _state with { Parameters = parameters, Profile = profile };
            return Task.FromResult(new DeviceCommandResult(Guid.NewGuid(), true, "updated", _state));
        }

        public Task<SnapCommandResult> SnapAsync(CancellationToken cancellationToken = default)
        {
            _state = State(DeviceStatus.Frozen, LastParameters, LastProfile);
            var measured = new MeasuredTelemetryContract(
                80,
                30,
                2400,
                1800,
                25,
                MeasurementProvenanceContract.Measured,
                MeasurementProvenanceContract.Derived,
                MeasurementProvenanceContract.Measured);
            var image = new RemoteImageMetadataContract(
                ImageId,
                AcquisitionId,
                nameof(FrameKind.Snap),
                2,
                2,
                4,
                nameof(Gray16PixelFormat.Gray16LittleEndian),
                2,
                2,
                DateTimeOffset.UtcNow,
                LastProfile ?? nameof(AcquisitionProfile.IndustrialQaPhantom),
                LastParameters ?? new AcquisitionParametersContract(70, 3500, 25),
                measured,
                true,
                true);
            return Task.FromResult(new SnapCommandResult(
                Guid.NewGuid(), true, "snap", _state, image));
        }

        public Task<IReadOnlyList<RemoteImageMetadataContract>> GetImagesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteImageMetadataContract>>([]);

        public Task<byte[]> GetLowImageAsync(Guid imageId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]> GetRawImageAsync(Guid imageId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ImageId, imageId);
            using var stream = new MemoryStream();
            Gray16RawFileCodec.Write(stream, CreateFrame(FrameKind.Snap, AcquisitionId));
            return Task.FromResult(stream.ToArray());
        }

        public void RaiseTelemetry(TelemetryEventContract telemetry) => TelemetryReceived?.Invoke(telemetry);
        public void RaiseLowFrame(RemoteLowFrame frame) => LowFrameReceived?.Invoke(frame);
        public void RaiseHardwareState(HardwareStateEventContract state) => HardwareStateReceived?.Invoke(state);
        public void RaiseConnectionState(bool connected) => ConnectionStateChanged?.Invoke(connected);
        public void SetReconnectState(long sequence)
        {
            _state = State(DeviceStatus.Connected) with
            {
                SequenceNumber = sequence,
                CoordinatorState = nameof(AcquisitionCoordinatorState.Idle),
                GeneratorConnected = true,
                DetectorConnected = true,
                XRayActive = false,
                DetectorAcquiring = false
            };
        }
        public void RaiseStatus(DeviceStatus status, long sequence)
        {
            var state = State(status) with { SequenceNumber = sequence };
            StatusChanged?.Invoke(state);
        }

        public TelemetryEventContract CreateTelemetry(long sequence) => new(
            "mock-01",
            sequence,
            nameof(DeviceStatus.Capturing),
            AcquisitionId,
            nameof(FrameKind.LivePreview),
            2,
            2,
            true,
            2,
            2,
            DateTimeOffset.UtcNow,
            nameof(AcquisitionProfile.IndustrialQaPhantom),
            new AcquisitionParametersContract(70, 3500, 10),
            new MeasuredTelemetryContract(70, 50, 3500, 1200, 10));

        public RawGray16Frame CreateFrame(FrameKind kind, Guid acquisitionId) => new(
            [0, 16384, 32768, ushort.MaxValue],
            2,
            2,
            4,
            Gray16PixelFormat.Gray16LittleEndian,
            kind,
            DateTimeOffset.UtcNow,
            acquisitionId,
            new AcquisitionSettings(
                30,
                80,
                25,
                LastProfile == nameof(AcquisitionProfile.MedicalSpecimenLikePhantom)
                    ? AcquisitionProfile.MedicalSpecimenLikePhantom
                    : AcquisitionProfile.IndustrialQaPhantom),
            "remote-test");

        private Task<DeviceCommandResult> Command(DeviceStatus status, string message)
        {
            _state = State(status, LastParameters, LastProfile);
            StatusChanged?.Invoke(_state);
            return Task.FromResult(new DeviceCommandResult(Guid.NewGuid(), true, message, _state));
        }

        private static DeviceStateContract State(
            DeviceStatus status,
            AcquisitionParametersContract? parameters = null,
            string? profile = null) => new(
            "mock-01",
            status.ToString(),
            true,
            parameters ?? new AcquisitionParametersContract(70, 3500, 500),
            1,
            DateTimeOffset.UtcNow,
            profile ?? nameof(AcquisitionProfile.IndustrialQaPhantom));
    }
}
