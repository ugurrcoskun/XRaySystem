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
public sealed class RemoteSensorService : IAcquisitionCoordinator, IDisposable
{
    private readonly IRemoteDeviceClient _client;
    private AcquisitionParameters _parameters = new(70, 3500, 500);
    private int _profile = (int)AcquisitionProfile.IndustrialQaPhantom;
    private int _currentStatus = (int)DeviceStatus.Disconnected;
    private TelemetryEventContract? _latestTelemetry;
    private int _initialConnectCompleted;
    private long _lastStateSequence = -1;
    private int _eventsConnected;
    private bool _isSimulator = true;
    private AcquisitionHardwareState _hardwareState = new(
        AcquisitionCoordinatorState.Disconnected,
        GeneratorConnected: false,
        DetectorConnected: false,
        XRayActive: false,
        DetectorAcquiring: false,
        ActiveAcquisitionId: null,
        IsVerified: false,
        VerifiedAtUtc: null);
    private bool _disposed;

    public RemoteSensorService(IRemoteDeviceClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.StatusChanged += OnRemoteStatusChanged;
        _client.HardwareStateReceived += OnRemoteHardwareStateReceived;
        _client.TelemetryReceived += OnTelemetryReceived;
        _client.LowFrameReceived += OnLowFrameReceived;
        _client.ErrorReceived += OnRemoteError;
        _client.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public DeviceStatus CurrentStatus => (DeviceStatus)Volatile.Read(ref _currentStatus);
    public AcquisitionCoordinatorState CoordinatorState => Volatile.Read(ref _hardwareState).CoordinatorState;
    public AcquisitionHardwareState HardwareState => Volatile.Read(ref _hardwareState);

    public event Action<DeviceStatus>? StatusChanged;
    public event Action<XRayMeasurement>? MeasurementReceived;
    public event Action<AcquisitionHardwareState>? HardwareStateChanged;

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
        await _client.ValidateProtocolAsync().ConfigureAwait(false);
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
            if (CurrentStatus is DeviceStatus.Grabbing or DeviceStatus.Snapping)
                await AbortAsync().ConfigureAwait(false);
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
        var result = await _client.StartGrabAsync().ConfigureAwait(false);
        EnsureSucceeded(result.Succeeded, result.Message);
        ApplyState(result.Device);
    }

    public async Task StopCaptureAsync()
    {
        ThrowIfDisposed();
        var result = await _client.FreezeGrabAsync().ConfigureAwait(false);
        EnsureSucceeded(result.Succeeded, result.Message);
        ApplyState(result.Device);
    }

    public async Task AbortAsync()
    {
        ThrowIfDisposed();
        var result = await _client.AbortAsync().ConfigureAwait(false);
        EnsureSucceeded(result.Succeeded, result.Message);
        ApplyState(result.Device);
    }

    public Task<XRayMeasurement> SnapAsync(
        double exposureTime,
        CancellationToken cancellationToken = default) =>
        SnapAsync(new SnapRequest(exposureTime, SnapFrameMode.Raw), cancellationToken);

    public async Task<XRayMeasurement> SnapAsync(
        SnapRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        var parameters = Parameters with { ExposureTimeMs = request.ExposureTimeMs };
        parameters.EnsureValid();
        Parameters = parameters;
        await PushParametersAsync(cancellationToken).ConfigureAwait(false);

        var contractMode = request.FrameMode == SnapFrameMode.Low
            ? SnapFrameModeContract.Low
            : SnapFrameModeContract.Raw;
        var result = await _client.SnapAsync(contractMode, cancellationToken).ConfigureAwait(false);
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
                measured.ExposureTimeMs,
                ToDomain(measured.TubeCurrentProvenance),
                ToDomain(measured.VoltageProvenance),
                ToDomain(measured.ExposureTimeProvenance)),
            frame,
            result.Device.IsSimulator,
            new AcquisitionParameters(
                result.Image.RequestedParameters.VoltageKv,
                result.Image.RequestedParameters.PowerW,
                result.Image.RequestedParameters.ExposureTimeMs));
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
        if (Volatile.Read(ref _eventsConnected) == 0) return;
        try { ApplyState(state, applyConfiguration: false); }
        catch { SetStatus(DeviceStatus.Error); }
    }

    private void OnRemoteHardwareStateReceived(HardwareStateEventContract state)
    {
        if (Volatile.Read(ref _eventsConnected) == 0) return;
        try
        {
            if (!AcceptStateSequence(state.SequenceNumber)) return;
            ApplyHardwareState(
                state.CoordinatorState,
                state.GeneratorConnected,
                state.DetectorConnected,
                state.XRayActive,
                state.DetectorAcquiring,
                state.ActiveAcquisitionId,
                state.TimestampUtc);
        }
        catch
        {
            ApplyHardwareState(
                nameof(AcquisitionCoordinatorState.Faulted),
                GeneratorConnected: false,
                DetectorConnected: false,
                XRayActive: false,
                DetectorAcquiring: false,
                ActiveAcquisitionId: null,
                VerifiedAtUtc: null);
        }
    }

    private void OnTelemetryReceived(TelemetryEventContract telemetry)
    {
        if (Volatile.Read(ref _eventsConnected) != 0)
            Volatile.Write(ref _latestTelemetry, telemetry);
    }

    private void OnLowFrameReceived(RemoteLowFrame remoteFrame)
    {
        if (Volatile.Read(ref _eventsConnected) == 0) return;
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
                FrameKind.GrabPreview,
                telemetry.SequenceNumber == remoteFrame.FrameSequence
                    ? telemetry.CapturedAtUtc
                    : DateTimeOffset.UtcNow,
                remoteFrame.AcquisitionId,
                new AcquisitionSettings(
                    measured.TubeCurrentMa,
                    measured.VoltageKv,
                    measured.ExposureTimeMs,
                    profile),
                $"remote-grab-low-bmp:{remoteFrame.FrameSequence}",
                sourceOrientation: "top-left",
                operation: AcquisitionOperation.Grab);
            var measurement = new XRayMeasurement(
                new SensorReadings(
                    measured.TubeCurrentMa,
                    measured.VoltageKv,
                    measured.XRayIntensity,
                    measured.ExposureTimeMs,
                    ToDomain(measured.TubeCurrentProvenance),
                    ToDomain(measured.VoltageProvenance),
                    ToDomain(measured.ExposureTimeProvenance)),
                frame,
                _isSimulator,
                new AcquisitionParameters(
                    telemetry.RequestedParameters.VoltageKv,
                    telemetry.RequestedParameters.PowerW,
                    telemetry.RequestedParameters.ExposureTimeMs));
            PublishMeasurement(measurement);
        }
        catch
        {
            SetStatus(DeviceStatus.Error);
        }
    }

    private void OnRemoteError(DeviceErrorEventContract _)
    {
        if (Volatile.Read(ref _eventsConnected) != 0)
            SetStatus(DeviceStatus.Error);
    }

    private void OnConnectionStateChanged(bool connected)
    {
        Volatile.Write(ref _eventsConnected, connected ? 1 : 0);
        if (!connected)
        {
            MarkHardwareStateUnverified();
            if (CurrentStatus != DeviceStatus.Disconnected) SetStatus(DeviceStatus.Error);
            return;
        }

        Interlocked.Exchange(ref _lastStateSequence, -1);
        if (Volatile.Read(ref _initialConnectCompleted) != 0)
            _ = RefreshStateAfterReconnectAsync();
    }

    private async Task RefreshStateAfterReconnectAsync()
    {
        try { ApplyState(await _client.GetStateAsync().ConfigureAwait(false)); }
        catch { SetStatus(DeviceStatus.Error); }
    }

    private void ApplyState(DeviceStateContract state, bool applyConfiguration = true)
    {
        if (!AcceptStateSequence(state.SequenceNumber)) return;
        if (!Enum.TryParse<DeviceStatus>(state.Status, ignoreCase: true, out var status) ||
            !Enum.IsDefined(status))
            throw new InvalidDataException($"Remote cihaz durumu geçersiz: {state.Status}");

        if (applyConfiguration)
        {
            var parameters = new AcquisitionParameters(
                state.Parameters.VoltageKv,
                state.Parameters.PowerW,
                state.Parameters.ExposureTimeMs);
            parameters.EnsureValid();
            Volatile.Write(ref _parameters, parameters);
            Volatile.Write(ref _profile, (int)ParseProfile(state.Profile));
        }
        _isSimulator = state.IsSimulator;
        ApplyHardwareState(
            state.CoordinatorState,
            state.GeneratorConnected,
            state.DetectorConnected,
            state.XRayActive,
            state.DetectorAcquiring,
            state.ActiveAcquisitionId,
            state.TimestampUtc);
        SetStatus(status);
    }

    private bool AcceptStateSequence(long sequence)
    {
        if (sequence < 0) throw new InvalidDataException("Remote cihaz sequence değeri negatif olamaz.");
        while (true)
        {
            var observed = Interlocked.Read(ref _lastStateSequence);
            if (sequence < observed) return false;
            if (sequence == observed ||
                Interlocked.CompareExchange(ref _lastStateSequence, sequence, observed) == observed)
                return true;
        }
    }

    private void ApplyHardwareState(
        string? coordinatorState,
        bool? GeneratorConnected,
        bool? DetectorConnected,
        bool? XRayActive,
        bool? DetectorAcquiring,
        Guid? ActiveAcquisitionId,
        DateTimeOffset? VerifiedAtUtc)
    {
        var parsedState = AcquisitionCoordinatorState.Disconnected;
        if (coordinatorState is not null &&
            (!Enum.TryParse(coordinatorState, ignoreCase: true, out parsedState) || !Enum.IsDefined(parsedState)))
            throw new InvalidDataException($"Remote acquisition coordinator durumu geçersiz: {coordinatorState}");
        var isVerified = coordinatorState is not null &&
            GeneratorConnected.HasValue && DetectorConnected.HasValue &&
            XRayActive.HasValue && DetectorAcquiring.HasValue &&
            Volatile.Read(ref _eventsConnected) != 0;
        var next = new AcquisitionHardwareState(
            parsedState,
            GeneratorConnected == true,
            DetectorConnected == true,
            XRayActive == true,
            DetectorAcquiring == true,
            ActiveAcquisitionId,
            isVerified,
            isVerified ? VerifiedAtUtc?.ToUniversalTime() : null);
        PublishHardwareState(next);
    }

    private void MarkHardwareStateUnverified()
    {
        var current = Volatile.Read(ref _hardwareState);
        PublishHardwareState(current with
        {
            CoordinatorState = AcquisitionCoordinatorState.Faulted,
            XRayActive = false,
            DetectorAcquiring = false,
            ActiveAcquisitionId = null,
            IsVerified = false,
            VerifiedAtUtc = null
        });
    }

    private void PublishHardwareState(AcquisitionHardwareState next)
    {
        var previous = Interlocked.Exchange(ref _hardwareState, next);
        if (previous == next) return;
        var handlers = HardwareStateChanged;
        if (handlers is null) return;
        foreach (Action<AcquisitionHardwareState> handler in handlers.GetInvocationList())
        {
            try { handler(next); }
            catch { }
        }
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

    private static MeasurementProvenance ToDomain(MeasurementProvenanceContract provenance) =>
        provenance switch
        {
            MeasurementProvenanceContract.Measured => MeasurementProvenance.Measured,
            MeasurementProvenanceContract.AppliedTarget => MeasurementProvenance.AppliedTarget,
            MeasurementProvenanceContract.Derived => MeasurementProvenance.Derived,
            _ => MeasurementProvenance.Unknown
        };

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
            !string.Equals(frame.Settings.Profile.ToString(), metadata.Profile, StringComparison.Ordinal) ||
            metadata.Operation != AcquisitionOperationContract.Snap ||
            metadata.SnapFrameMode is null ||
            frame.Operation != AcquisitionOperation.Snap ||
            frame.SnapFrameMode != (metadata.SnapFrameMode == SnapFrameModeContract.Low
                ? SnapFrameMode.Low
                : SnapFrameMode.Raw))
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
        _client.HardwareStateReceived -= OnRemoteHardwareStateReceived;
        _client.TelemetryReceived -= OnTelemetryReceived;
        _client.LowFrameReceived -= OnLowFrameReceived;
        _client.ErrorReceived -= OnRemoteError;
        _client.ConnectionStateChanged -= OnConnectionStateChanged;
    }
}
