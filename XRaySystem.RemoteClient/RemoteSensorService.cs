using XRaySystem.Contracts;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Imaging;
using XRaySystem.Infrastructure.Storage;

namespace XRaySystem.RemoteClient;

/// <summary>
/// Adapts DeviceHost REST/SignalR contracts to the existing sensor-neutral
/// domain interface so WPF and MAUI ViewModels can run unchanged in remote mode.
/// </summary>
public sealed class RemoteSensorService : ISensorService, IDisposable
{
    private readonly IRemoteDeviceClient _client;
    private AcquisitionParameters _parameters = new(70, 3500, 500);
    private int _profile = (int)AcquisitionProfile.IndustrialQaPhantom;
    private int _currentStatus = (int)DeviceStatus.Disconnected;
    private TelemetryEventContract? _latestTelemetry;
    private int _initialConnectCompleted;
    private bool _isSimulator = true;
    private bool _disposed;

    public RemoteSensorService(IRemoteDeviceClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.StatusChanged += OnRemoteStatusChanged;
        _client.TelemetryReceived += OnTelemetryReceived;
        _client.LowFrameReceived += OnLowFrameReceived;
        _client.ErrorReceived += OnRemoteError;
        _client.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public DeviceStatus CurrentStatus => (DeviceStatus)Volatile.Read(ref _currentStatus);

    public event Action<DeviceStatus>? StatusChanged;
    public event Action<XRayMeasurement>? MeasurementReceived;

    public AcquisitionProfile Profile
    {
        get => (AcquisitionProfile)Volatile.Read(ref _profile);
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _profile, (int)value);
        }
    }

    public AcquisitionParameters Parameters
    {
        get => Volatile.Read(ref _parameters);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.EnsureValid();
            Volatile.Write(ref _parameters, value);
        }
    }

    public async Task ConnectAsync()
    {
        ThrowIfDisposed();
        await _client.ConnectEventsAsync().ConfigureAwait(false);
        var result = await _client.ConnectDeviceAsync().ConfigureAwait(false);
        EnsureSucceeded(result.Succeeded, result.Message);
        ApplyState(result.Device);
        Volatile.Write(ref _initialConnectCompleted, 1);
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;
        try
        {
            var result = await _client.DisconnectDeviceAsync().ConfigureAwait(false);
            EnsureSucceeded(result.Succeeded, result.Message);
            ApplyState(result.Device);
            Volatile.Write(ref _initialConnectCompleted, 0);
        }
        finally
        {
            await _client.DisconnectEventsAsync().ConfigureAwait(false);
        }
    }

    public async Task StartCaptureAsync()
    {
        ThrowIfDisposed();
        await PushParametersAsync().ConfigureAwait(false);
        var result = await _client.StartLiveAsync().ConfigureAwait(false);
        EnsureSucceeded(result.Succeeded, result.Message);
        ApplyState(result.Device);
    }

    public async Task StopCaptureAsync()
    {
        ThrowIfDisposed();
        var result = await _client.StopLiveAsync().ConfigureAwait(false);
        EnsureSucceeded(result.Succeeded, result.Message);
        ApplyState(result.Device);
    }

    public async Task<XRayMeasurement> SnapAsync(
        double exposureTime,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var parameters = Parameters with { ExposureTimeMs = exposureTime };
        parameters.EnsureValid();
        Parameters = parameters;
        await PushParametersAsync(cancellationToken).ConfigureAwait(false);

        var result = await _client.SnapAsync(cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result.Succeeded, result.Message);
        ApplyState(result.Device);
        var rawBytes = await _client
            .GetRawImageAsync(result.Image.ImageId, cancellationToken)
            .ConfigureAwait(false);
        using var stream = new MemoryStream(rawBytes, writable: false);
        var frame = Gray16RawFileCodec.Read(stream);
        EnsureRawMatchesMetadata(frame, result.Image);

        var measured = result.Image.MeasuredTelemetry;
        var measurement = new XRayMeasurement(
            result.Image.ImageId,
            result.Image.CapturedAtUtc.UtcDateTime,
            new SensorReadings(
                measured.TubeCurrentMa,
                measured.VoltageKv,
                measured.XRayIntensity,
                measured.ExposureTimeMs),
            frame,
            result.Device.IsSimulator);
        PublishMeasurement(measurement);
        return measurement;
    }

    public Task ResumeLiveAsync() => StartCaptureAsync();

    private async Task PushParametersAsync(CancellationToken cancellationToken = default)
    {
        var parameters = Parameters;
        var result = await _client.UpdateParametersAsync(
            new AcquisitionParametersContract(
                parameters.VoltageKv,
                parameters.PowerW,
                parameters.ExposureTimeMs),
            Profile.ToString(),
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result.Succeeded, result.Message);
        ApplyState(result.Device);
    }

    private void OnRemoteStatusChanged(DeviceStateContract state)
    {
        try { ApplyState(state); }
        catch { SetStatus(DeviceStatus.Error); }
    }

    private void OnTelemetryReceived(TelemetryEventContract telemetry) =>
        Volatile.Write(ref _latestTelemetry, telemetry);

    private void OnLowFrameReceived(RemoteLowFrame remoteFrame)
    {
        try
        {
            var decoded = Gray8BmpDecoder.Decode(remoteFrame.BmpBytes);
            if (decoded.Width != remoteFrame.Width || decoded.Height != remoteFrame.Height)
                throw new InvalidDataException("Remote live BMP boyutları response başlıklarıyla uyuşmuyor.");

            var telemetry = Volatile.Read(ref _latestTelemetry);
            if (telemetry?.SequenceNumber != remoteFrame.FrameSequence)
                telemetry = remoteFrame.TriggerTelemetry;
            var measured = telemetry.MeasuredTelemetry;
            var profile = ParseProfile(telemetry.Profile);
            var frame = new RawGray16Frame(
                Gray8BmpDecoder.ExpandToGray16(decoded.Pixels),
                decoded.Width,
                decoded.Height,
                checked(decoded.Width * sizeof(ushort)),
                Gray16PixelFormat.Gray16LittleEndian,
                FrameKind.LivePreview,
                telemetry.SequenceNumber == remoteFrame.FrameSequence
                    ? telemetry.CapturedAtUtc
                    : DateTimeOffset.UtcNow,
                remoteFrame.AcquisitionId,
                new AcquisitionSettings(
                    measured.TubeCurrentMa,
                    measured.VoltageKv,
                    measured.ExposureTimeMs,
                    profile),
                $"remote-low-bmp:{remoteFrame.FrameSequence}",
                sourceOrientation: "top-left");
            var measurement = new XRayMeasurement(
                new SensorReadings(
                    measured.TubeCurrentMa,
                    measured.VoltageKv,
                    measured.XRayIntensity,
                    measured.ExposureTimeMs),
                frame,
                _isSimulator);
            PublishMeasurement(measurement);
        }
        catch
        {
            SetStatus(DeviceStatus.Error);
        }
    }

    private void OnRemoteError(DeviceErrorEventContract _) => SetStatus(DeviceStatus.Error);

    private void OnConnectionStateChanged(bool connected)
    {
        if (!connected)
        {
            if (CurrentStatus != DeviceStatus.Disconnected) SetStatus(DeviceStatus.Error);
            return;
        }

        if (Volatile.Read(ref _initialConnectCompleted) != 0)
            _ = RefreshStateAfterReconnectAsync();
    }

    private async Task RefreshStateAfterReconnectAsync()
    {
        try { ApplyState(await _client.GetStateAsync().ConfigureAwait(false)); }
        catch { SetStatus(DeviceStatus.Error); }
    }

    private void ApplyState(DeviceStateContract state)
    {
        if (!Enum.TryParse<DeviceStatus>(state.Status, ignoreCase: true, out var status) ||
            !Enum.IsDefined(status))
            throw new InvalidDataException($"Remote cihaz durumu geçersiz: {state.Status}");

        var parameters = new AcquisitionParameters(
            state.Parameters.VoltageKv,
            state.Parameters.PowerW,
            state.Parameters.ExposureTimeMs);
        parameters.EnsureValid();
        Volatile.Write(ref _parameters, parameters);
        Volatile.Write(ref _profile, (int)ParseProfile(state.Profile));
        _isSimulator = state.IsSimulator;
        SetStatus(status);
    }

    private void SetStatus(DeviceStatus status)
    {
        var old = (DeviceStatus)Interlocked.Exchange(ref _currentStatus, (int)status);
        if (old == status) return;
        var handlers = StatusChanged;
        if (handlers is null) return;
        foreach (Action<DeviceStatus> handler in handlers.GetInvocationList())
        {
            try { handler(status); }
            catch { }
        }
    }

    private void PublishMeasurement(XRayMeasurement measurement)
    {
        var handlers = MeasurementReceived;
        if (handlers is null) return;
        foreach (Action<XRayMeasurement> handler in handlers.GetInvocationList())
        {
            try { handler(measurement); }
            catch { }
        }
    }

    private static AcquisitionProfile ParseProfile(string profile)
    {
        if (!Enum.TryParse<AcquisitionProfile>(profile, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
            throw new InvalidDataException($"Remote sensör profili geçersiz: {profile}");
        return parsed;
    }

    private static void EnsureRawMatchesMetadata(
        RawGray16Frame frame,
        RemoteImageMetadataContract metadata)
    {
        if (frame.AcquisitionId != metadata.AcquisitionId ||
            frame.Width != metadata.Width ||
            frame.Height != metadata.Height ||
            frame.StrideBytes != metadata.StrideBytes ||
            !string.Equals(frame.PixelFormat.ToString(), metadata.PixelFormat, StringComparison.Ordinal) ||
            !string.Equals(frame.Kind.ToString(), metadata.Kind, StringComparison.Ordinal) ||
            !string.Equals(frame.Settings.Profile.ToString(), metadata.Profile, StringComparison.Ordinal))
            throw new InvalidDataException("Remote raw görüntü ve SNAP metadata uyuşmuyor.");
    }

    private static void EnsureSucceeded(bool succeeded, string message)
    {
        if (!succeeded) throw new InvalidOperationException(message);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.StatusChanged -= OnRemoteStatusChanged;
        _client.TelemetryReceived -= OnTelemetryReceived;
        _client.LowFrameReceived -= OnLowFrameReceived;
        _client.ErrorReceived -= OnRemoteError;
        _client.ConnectionStateChanged -= OnConnectionStateChanged;
    }
}
