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
        Assert.Equal("remote-low-bmp:7", received.Frame.SourceMarker);
        Assert.Equal(source.Width * source.Height, received.Frame.Pixels.Length);
        Assert.Equal(DeviceStatus.Connected, service.CurrentStatus);
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
            var measured = new MeasuredTelemetryContract(80, 30, 2400, 1800, 25);
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
