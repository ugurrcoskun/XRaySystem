using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Sensors;

/// <summary>
/// The device-side safety owner for separate generator and detector adapters.
/// Network/UI code must never sequence these adapters directly.
/// </summary>
public sealed class CoordinatedHardwareSensorService : IAcquisitionCoordinator, IDisposable
{
    private readonly IXRayGenerator _generator;
    private readonly IXRayDetector _detector;
    private readonly AcquisitionSafetyOptions _safety;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _stateGate = new();
    private AcquisitionParameters _parameters = new(70, 210, 500);
    private DeviceStatus _currentStatus = DeviceStatus.Disconnected;
    private AcquisitionCoordinatorState _coordinatorState = AcquisitionCoordinatorState.Disconnected;
    private CancellationTokenSource? _activeCancellation;
    private Guid? _activeAcquisitionId;
    private Guid? _grabSessionId;
    private Task? _grabLoop;
    private Task? _grabWatchdog;
    private bool _acceptGrabFrames;
    private bool _disposed;

    public CoordinatedHardwareSensorService(
        IXRayGenerator generator,
        IXRayDetector detector,
        AcquisitionSafetyOptions? safety = null)
    {
        _generator = generator;
        _detector = detector;
        _safety = safety ?? AcquisitionSafetyOptions.Default;
        _safety.EnsureValid();
    }

    public DeviceStatus CurrentStatus { get { lock (_stateGate) return _currentStatus; } }
    public AcquisitionCoordinatorState CoordinatorState { get { lock (_stateGate) return _coordinatorState; } }
    public AcquisitionHardwareState HardwareState
    {
        get
        {
            lock (_stateGate)
            {
                return new AcquisitionHardwareState(
                    _coordinatorState,
                    _generator.IsConnected,
                    _detector.IsConnected,
                    _generator.IsExposureActive,
                    _detector.IsAcquiring,
                    _activeAcquisitionId);
            }
        }
    }
    public AcquisitionProfile Profile { get; set; } = AcquisitionProfile.IndustrialQaPhantom;
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

    public event Action<DeviceStatus>? StatusChanged;
    public event Action<XRayMeasurement>? MeasurementReceived;
    public event Action<AcquisitionHardwareState>? HardwareStateChanged;

    public async Task ConnectAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CurrentStatus != DeviceStatus.Disconnected) return;
            try
            {
                await _generator.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await _detector.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                SetState(AcquisitionCoordinatorState.Idle, DeviceStatus.Connected);
            }
            catch
            {
                await SafeDisconnectHardwareAsync().ConfigureAwait(false);
                SetState(AcquisitionCoordinatorState.Faulted, DeviceStatus.Error);
                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        ThrowIfDisposed();
        if (CurrentStatus != DeviceStatus.Disconnected)
            await AbortAsync().ConfigureAwait(false);

        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SafeDisconnectHardwareAsync().ConfigureAwait(false);
            SetState(AcquisitionCoordinatorState.Disconnected, DeviceStatus.Disconnected);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StartCaptureAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync().ConfigureAwait(false);
        CancellationTokenSource? acquisitionCancellation = null;
        try
        {
            EnsureReadyForAcquisition();
            var parameters = Parameters;
            parameters.EnsureValid();
            var sessionId = Guid.NewGuid();
            acquisitionCancellation = new CancellationTokenSource();
            SetActiveAcquisition(sessionId, acquisitionCancellation, isGrab: true);
            var token = acquisitionCancellation.Token;

            SetState(AcquisitionCoordinatorState.Configuring, DeviceStatus.Connected);
            await _detector.ConfigureGrabAsync(
                sessionId,
                new DetectorGrabSettings(parameters.ExposureTimeMs),
                token).ConfigureAwait(false);
            await _generator.ApplyTechniqueAsync(
                new GeneratorTechnique(parameters.VoltageKv, parameters.PowerW),
                token).ConfigureAwait(false);

            await _detector.ArmAsync(sessionId, token).ConfigureAwait(false);
            await RunStageAsync(
                ct => _detector.WaitUntilArmedAsync(sessionId, ct),
                _safety.DetectorArmTimeout,
                "detector.arm",
                token).ConfigureAwait(false);

            SetState(AcquisitionCoordinatorState.Preparing, DeviceStatus.Connected);
            await _generator.PrepareAsync(token).ConfigureAwait(false);
            await RunStageAsync(
                _generator.WaitUntilReadyAsync,
                _safety.GeneratorReadyTimeout,
                "generator.ready",
                token).ConfigureAwait(false);

            SetState(AcquisitionCoordinatorState.Armed, DeviceStatus.Connected);
            await _detector.StartGrabAsync(sessionId, token).ConfigureAwait(false);
            await RunStageAsync(
                ct => _generator.StartExposureAsync(sessionId, ct),
                _safety.ExposureStartTimeout,
                "generator.exposure-start",
                token).ConfigureAwait(false);

            lock (_stateGate) _acceptGrabFrames = true;
            SetState(AcquisitionCoordinatorState.Grabbing, DeviceStatus.Grabbing);
            _grabLoop = RunGrabLoopAsync(sessionId, parameters, token);
            _grabWatchdog = RunGrabWatchdogAsync(sessionId, acquisitionCancellation, token);
            acquisitionCancellation = null;
        }
        catch
        {
            if (acquisitionCancellation is not null)
                ClearActiveAcquisition(acquisitionCancellation);
            await SafeStopAllBestEffortAsync().ConfigureAwait(false);
            SetFailureOrCancellationState();
            throw;
        }
        finally
        {
            acquisitionCancellation?.Dispose();
            _operationLock.Release();
        }
    }

    public async Task StopCaptureAsync()
    {
        ThrowIfDisposed();
        if (CurrentStatus == DeviceStatus.Snapping)
        {
            await AbortAsync().ConfigureAwait(false);
            return;
        }

        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CurrentStatus != DeviceStatus.Grabbing) return;
            await FreezeGrabCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<XRayMeasurement> SnapAsync(
        double exposureTime,
        CancellationToken cancellationToken = default) =>
        SnapAsync(new SnapRequest(exposureTime, SnapFrameMode.Raw), cancellationToken);

    public async Task<XRayMeasurement> SnapAsync(
        SnapRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        ThrowIfDisposed();
        if (!_detector.SupportsSnapMode(request.FrameMode))
            throw new NotSupportedException($"Detector SNAP {request.FrameMode} modunu desteklemiyor.");
        var exposureDuration = TimeSpan.FromMilliseconds(request.ExposureTimeMs);
        if (exposureDuration > _safety.MaximumExposureDuration)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Exposure süresi güvenlik üst sınırını aşıyor ({_safety.MaximumExposureDuration.TotalMilliseconds:0} ms).");

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? abortCancellation = null;
        DetectorFrameToken? frameToken = null;
        try
        {
            if (CurrentStatus == DeviceStatus.Grabbing)
                await FreezeGrabCoreAsync().ConfigureAwait(false);
            EnsureReadyForAcquisition();

            var acquisitionId = Guid.NewGuid();
            var parameters = Parameters;
            var technique = new GeneratorTechnique(parameters.VoltageKv, parameters.PowerW);
            var detectorSettings = new DetectorSnapSettings(request.ExposureTimeMs, request.FrameMode);
            technique.EnsureValid();
            detectorSettings.EnsureValid();

            abortCancellation = new CancellationTokenSource();
            SetActiveAcquisition(acquisitionId, abortCancellation, isGrab: false);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                abortCancellation.Token);
            var token = linkedCancellation.Token;

            SetState(AcquisitionCoordinatorState.Configuring, DeviceStatus.Snapping);
            await _detector.ConfigureSnapAsync(acquisitionId, detectorSettings, token).ConfigureAwait(false);
            await _generator.ApplyTechniqueAsync(technique, token).ConfigureAwait(false);

            await _detector.ArmAsync(acquisitionId, token).ConfigureAwait(false);
            await RunStageAsync(
                ct => _detector.WaitUntilArmedAsync(acquisitionId, ct),
                _safety.DetectorArmTimeout,
                "detector.arm",
                token).ConfigureAwait(false);

            SetState(AcquisitionCoordinatorState.Preparing, DeviceStatus.Snapping);
            await _generator.PrepareAsync(token).ConfigureAwait(false);
            await RunStageAsync(
                _generator.WaitUntilReadyAsync,
                _safety.GeneratorReadyTimeout,
                "generator.ready",
                token).ConfigureAwait(false);

            SetState(AcquisitionCoordinatorState.Armed, DeviceStatus.Snapping);
            await _detector.StartSnapAsync(acquisitionId, token).ConfigureAwait(false);
            await RunStageAsync(
                ct => _generator.StartExposureAsync(acquisitionId, ct),
                _safety.ExposureStartTimeout,
                "generator.exposure-start",
                token).ConfigureAwait(false);
            SetState(AcquisitionCoordinatorState.Exposing, DeviceStatus.Snapping);

            try
            {
                await Task.Delay(exposureDuration, token).ConfigureAwait(false);
            }
            finally
            {
                await StopGeneratorRequiredAsync().ConfigureAwait(false);
            }

            SetState(AcquisitionCoordinatorState.ReadingOut, DeviceStatus.Snapping);
            frameToken = await RunStageAsync(
                _detector.WaitForFrameAsync,
                _safety.FrameReadyTimeout,
                "detector.frame-ready",
                token).ConfigureAwait(false);
            if (frameToken.AcquisitionId != acquisitionId)
                throw new InvalidDataException("Late veya farklı acquisition'a ait detector frame'i reddedildi.");

            var completedFrameToken = frameToken;
            var pixels = await RunStageAsync(
                ct => _detector.CopyFrameAsync(frameToken, ct),
                _safety.BufferCopyTimeout,
                "detector.buffer-copy",
                token).ConfigureAwait(false);
            ValidateCopiedFrame(frameToken, pixels);
            await ReleaseFrameRequiredAsync(frameToken).ConfigureAwait(false);
            frameToken = null;

            var telemetry = await _generator.ReadTelemetryAsync(token).ConfigureAwait(false);
            var measurement = CreateMeasurement(
                acquisitionId,
                completedFrameToken,
                pixels,
                request.FrameMode,
                parameters,
                telemetry,
                isGrab: false,
                width: completedFrameToken.Width,
                height: completedFrameToken.Height,
                sourceMarker: completedFrameToken.SourceMarker);

            await StopDetectorRequiredAsync().ConfigureAwait(false);
            SetState(AcquisitionCoordinatorState.Frozen, DeviceStatus.Frozen);
            MeasurementReceived?.Invoke(measurement);
            return measurement;
        }
        catch (OperationCanceledException)
        {
            await SafeStopAllBestEffortAsync().ConfigureAwait(false);
            if (_generator.IsConnected && _detector.IsConnected)
                SetState(AcquisitionCoordinatorState.Idle, DeviceStatus.Connected);
            throw;
        }
        catch
        {
            await SafeStopAllBestEffortAsync().ConfigureAwait(false);
            SetState(AcquisitionCoordinatorState.Faulted, DeviceStatus.Error);
            throw;
        }
        finally
        {
            if (frameToken is not null)
                await ReleaseFrameBestEffortAsync(frameToken).ConfigureAwait(false);
            if (abortCancellation is not null)
            {
                ClearActiveAcquisition(abortCancellation);
                abortCancellation.Dispose();
            }
            _operationLock.Release();
        }
    }

    public Task ResumeLiveAsync() => StartCaptureAsync();

    public async Task AbortAsync()
    {
        ThrowIfDisposed();
        CancellationTokenSource? active;
        Guid? grabSession;
        Task? grabLoop;
        lock (_stateGate)
        {
            if (_currentStatus == DeviceStatus.Disconnected) return;
            _coordinatorState = AcquisitionCoordinatorState.Aborting;
            _acceptGrabFrames = false;
            active = _activeCancellation;
            grabSession = _grabSessionId;
            grabLoop = _grabLoop;
        }
        PublishHardwareState();
        active?.Cancel();

        // Safety order: generator OFF first, then detector freeze/stop.
        await SafeStopGeneratorBestEffortAsync().ConfigureAwait(false);
        if (grabSession is { } session)
        {
            try
            {
                await RunStageAsync(
                    ct => _detector.FreezeAsync(session, ct),
                    _safety.DetectorStopTimeout,
                    "detector.freeze",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }
        await SafeStopDetectorBestEffortAsync().ConfigureAwait(false);
        if (grabLoop is not null)
        {
            try { await grabLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (active is not null)
            {
                ClearActiveAcquisition(active);
                active.Dispose();
            }
            if (_generator.IsConnected && _detector.IsConnected)
                SetState(AcquisitionCoordinatorState.Idle, DeviceStatus.Connected);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task FreezeGrabCoreAsync()
    {
        Guid? session;
        CancellationTokenSource? cancellation;
        Task? grabLoop;
        lock (_stateGate)
        {
            session = _grabSessionId;
            cancellation = _activeCancellation;
            grabLoop = _grabLoop;
            _acceptGrabFrames = false;
            _coordinatorState = AcquisitionCoordinatorState.Freezing;
        }
        PublishHardwareState();
        cancellation?.Cancel();

        Exception? freezeFailure = null;
        try
        {
            if (session is { } sessionId)
                await RunStageAsync(
                    ct => _detector.FreezeAsync(sessionId, ct),
                    _safety.DetectorStopTimeout,
                    "detector.freeze",
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            freezeFailure = ex;
        }

        // Generator stop must run even when detector freeze fails.
        await StopGeneratorRequiredAsync().ConfigureAwait(false);
        await StopDetectorRequiredAsync().ConfigureAwait(false);
        if (grabLoop is not null)
        {
            try { await grabLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (cancellation is not null)
        {
            ClearActiveAcquisition(cancellation);
            cancellation.Dispose();
        }
        SetState(AcquisitionCoordinatorState.Frozen, DeviceStatus.Frozen);
        if (freezeFailure is not null)
            throw new InvalidOperationException("Detector FREEZE başarısız oldu; generator güvenli durduruldu.", freezeFailure);
    }

    private async Task RunGrabLoopAsync(
        Guid sessionId,
        AcquisitionParameters parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frameToken = await _detector.WaitForFrameAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    bool accept;
                    lock (_stateGate)
                        accept = _acceptGrabFrames && _grabSessionId == sessionId;
                    if (!accept || frameToken.AcquisitionId != sessionId)
                        continue;

                    var pixels = await RunStageAsync(
                        ct => _detector.CopyFrameAsync(frameToken, ct),
                        _safety.BufferCopyTimeout,
                        "detector.grab-buffer-copy",
                        cancellationToken).ConfigureAwait(false);
                    ValidateCopiedFrame(frameToken, pixels);
                    var telemetry = await _generator.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);
                    var measurement = CreateMeasurement(
                        sessionId,
                        frameToken,
                        pixels,
                        SnapFrameMode.Low,
                        parameters,
                        telemetry,
                        isGrab: true,
                        frameToken.Width,
                        frameToken.Height,
                        frameToken.SourceMarker);
                    lock (_stateGate)
                        accept = _acceptGrabFrames && _grabSessionId == sessionId;
                    if (accept) MeasurementReceived?.Invoke(measurement);
                }
                finally
                {
                    await ReleaseFrameBestEffortAsync(frameToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await SafeStopAllBestEffortAsync().ConfigureAwait(false);
            SetState(AcquisitionCoordinatorState.Faulted, DeviceStatus.Error);
        }
    }

    private async Task RunGrabWatchdogAsync(
        Guid sessionId,
        CancellationTokenSource acquisitionCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_safety.MaximumGrabDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        bool isStillActive;
        lock (_stateGate)
            isStillActive = _grabSessionId == sessionId &&
                ReferenceEquals(_activeCancellation, acquisitionCancellation);
        if (!isStillActive) return;

        await AbortAsync().ConfigureAwait(false);
        SetState(AcquisitionCoordinatorState.Faulted, DeviceStatus.Error);
    }

    private XRayMeasurement CreateMeasurement(
        Guid acquisitionId,
        DetectorFrameToken? frameToken,
        ushort[] pixels,
        SnapFrameMode frameMode,
        AcquisitionParameters parameters,
        GeneratorTelemetry telemetry,
        bool isGrab,
        int width,
        int height,
        string sourceMarker)
    {
        var settings = new AcquisitionSettings(
            telemetry.TubeCurrentMa,
            telemetry.VoltageKv,
            parameters.ExposureTimeMs,
            Profile);
        var frame = new RawGray16Frame(
            pixels,
            width,
            height,
            checked(width * sizeof(ushort)),
            Gray16PixelFormat.Gray16LittleEndian,
            isGrab ? FrameKind.GrabPreview : FrameKind.Snap,
            DateTimeOffset.UtcNow,
            acquisitionId,
            settings,
            sourceMarker,
            sourceStrideBytes: frameToken?.StrideBytes,
            sourceEndian: "little",
            sourceOrientation: "top-left",
            operation: isGrab ? AcquisitionOperation.Grab : AcquisitionOperation.Snap,
            snapFrameMode: isGrab ? null : frameMode);
        return new XRayMeasurement(
            new SensorReadings(
                telemetry.TubeCurrentMa,
                telemetry.VoltageKv,
                telemetry.XRayIntensity,
                parameters.ExposureTimeMs,
                telemetry.TubeCurrentProvenance,
                telemetry.VoltageProvenance,
                telemetry.ExposureTimeProvenance),
            frame,
            isSimulator: true,
            requestedParameters: parameters);
    }

    private static void ValidateCopiedFrame(DetectorFrameToken token, ushort[] pixels)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(pixels);
        if (token.BufferTokenId == Guid.Empty || token.AcquisitionId == Guid.Empty)
            throw new InvalidDataException("Detector frame token kimliği geçersiz.");
        if (token.Width <= 0 || token.Height <= 0)
            throw new InvalidDataException("Detector frame boyutları geçersiz.");
        if (token.PixelFormat != Gray16PixelFormat.Gray16LittleEndian)
            throw new InvalidDataException("Yalnız canonical Gray16 little-endian frame destekleniyor.");
        if (token.StrideBytes != checked(token.Width * sizeof(ushort)))
            throw new InvalidDataException("Detector frame stride geçersiz.");
        if (pixels.Length != checked(token.Width * token.Height))
            throw new InvalidDataException("Detector buffer uzunluğu frame boyutlarıyla uyuşmuyor.");
    }

    private async Task StopGeneratorRequiredAsync()
    {
        await RunStageAsync(
            _generator.StopExposureAsync,
            _safety.ExposureStopConfirmationTimeout,
            "generator.stop-command",
            CancellationToken.None).ConfigureAwait(false);
        await RunStageAsync(
            _generator.WaitUntilExposureStoppedAsync,
            _safety.ExposureStopConfirmationTimeout,
            "generator.stop-confirmation",
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SafeStopGeneratorBestEffortAsync()
    {
        try { await StopGeneratorRequiredAsync().ConfigureAwait(false); }
        catch { }
    }

    private async Task SafeStopAllBestEffortAsync()
    {
        await SafeStopGeneratorBestEffortAsync().ConfigureAwait(false);
        await SafeStopDetectorBestEffortAsync().ConfigureAwait(false);
    }

    private async Task StopDetectorRequiredAsync() =>
        await RunStageAsync(
            _detector.StopAsync,
            _safety.DetectorStopTimeout,
            "detector.stop",
            CancellationToken.None).ConfigureAwait(false);

    private async Task SafeStopDetectorBestEffortAsync()
    {
        try { await StopDetectorRequiredAsync().ConfigureAwait(false); }
        catch { }
    }

    private async Task ReleaseFrameRequiredAsync(DetectorFrameToken token) =>
        await RunStageAsync(
            ct => _detector.ReleaseFrameAsync(token, ct),
            _safety.BufferCopyTimeout,
            "detector.buffer-release",
            CancellationToken.None).ConfigureAwait(false);

    private async Task ReleaseFrameBestEffortAsync(DetectorFrameToken token)
    {
        try { await ReleaseFrameRequiredAsync(token).ConfigureAwait(false); }
        catch { }
    }

    private static async Task RunStageAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        string stage,
        CancellationToken cancellationToken)
    {
        using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var task = operation(stageCancellation.Token);
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stageCancellation.Cancel();
            throw new AcquisitionTimeoutException(stage, timeout);
        }
    }

    private static async Task<T> RunStageAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        string stage,
        CancellationToken cancellationToken)
    {
        using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var task = operation(stageCancellation.Token);
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stageCancellation.Cancel();
            throw new AcquisitionTimeoutException(stage, timeout);
        }
    }

    private void SetActiveAcquisition(Guid id, CancellationTokenSource cancellation, bool isGrab)
    {
        lock (_stateGate)
        {
            _activeAcquisitionId = id;
            _activeCancellation = cancellation;
            _grabSessionId = isGrab ? id : null;
            _acceptGrabFrames = false;
        }
    }

    private void ClearActiveAcquisition(CancellationTokenSource cancellation)
    {
        var changed = false;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_activeCancellation, cancellation)) return;
            cancellation.Cancel();
            _activeCancellation = null;
            _activeAcquisitionId = null;
            _grabSessionId = null;
            _grabLoop = null;
            _grabWatchdog = null;
            _acceptGrabFrames = false;
            changed = true;
        }
        if (changed) PublishHardwareState();
    }

    private void EnsureReadyForAcquisition()
    {
        if (!_generator.IsConnected || !_detector.IsConnected)
            throw new InvalidOperationException("Generator ve detector bağlı olmalıdır.");
        if (CurrentStatus is not (DeviceStatus.Connected or DeviceStatus.Frozen))
            throw new InvalidOperationException($"Acquisition mevcut durumda başlatılamaz: {CurrentStatus}.");
    }

    private void SetFailureOrCancellationState()
    {
        if (_generator.IsConnected && _detector.IsConnected)
            SetState(AcquisitionCoordinatorState.Faulted, DeviceStatus.Error);
        else
            SetState(AcquisitionCoordinatorState.Disconnected, DeviceStatus.Disconnected);
    }

    private void SetState(AcquisitionCoordinatorState coordinatorState, DeviceStatus status)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _coordinatorState != coordinatorState || _currentStatus != status;
            _coordinatorState = coordinatorState;
            _currentStatus = status;
        }
        if (changed)
        {
            StatusChanged?.Invoke(status);
            PublishHardwareState();
        }
    }

    private void PublishHardwareState()
    {
        var state = HardwareState;
        var handlers = HardwareStateChanged;
        if (handlers is null) return;
        foreach (Action<AcquisitionHardwareState> handler in handlers.GetInvocationList())
        {
            try { handler(state); }
            catch { }
        }
    }

    private async Task SafeDisconnectHardwareAsync()
    {
        await SafeStopAllBestEffortAsync().ConfigureAwait(false);
        try { await _detector.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
        try { await _generator.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_stateGate)
        {
            _acceptGrabFrames = false;
            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = null;
        }
        _operationLock.Dispose();
        if (_detector is IDisposable disposableDetector) disposableDetector.Dispose();
        if (_generator is IDisposable disposableGenerator) disposableGenerator.Dispose();
    }
}
