using XRaySystem.Contracts;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Domain.WindowLevel;
using XRaySystem.Infrastructure.Imaging;
using System.Runtime.ExceptionServices;
using ContractSnapFrameMode = XRaySystem.Contracts.SnapFrameModeContract;
using DomainSnapFrameMode = XRaySystem.Domain.ValueObjects.SnapFrameMode;

namespace XRaySystem.DeviceHost;

/// <summary>
/// Serializes all commands for the single physical/mock sensor owned by this host.
/// Network DTOs never expose local file paths or raw pixel arrays.
/// </summary>
public sealed class DeviceCoordinator : IDisposable
{
    private const int CompletedCommandLimit = 256;

    private readonly ISensorService _sensor;
    private readonly DeviceHostOptions _options;
    private readonly RemoteImageBuffer _imageBuffer;
    private readonly IDeviceEventPublisher _eventPublisher;
    private readonly IDeviceAcquisitionArchive _acquisitionArchive;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _abortLock = new(1, 1);
    private readonly object _completedCommandsGate = new();
    private readonly Dictionary<Guid, CompletedCommand> _completedCommands = [];
    private readonly Queue<Guid> _completedCommandOrder = [];
    private long _sequenceNumber;
    private bool _disposed;

    private sealed record CompletedCommand(string Operation, object Result);
    private sealed record CachedCommandFailure(Exception Exception);

    public DeviceCoordinator(
        ISensorService sensor,
        DeviceHostOptions options,
        RemoteImageBuffer imageBuffer,
        IDeviceEventPublisher? eventPublisher = null,
        IDeviceAcquisitionArchive? acquisitionArchive = null)
    {
        _sensor = sensor;
        _options = options;
        _imageBuffer = imageBuffer;
        _eventPublisher = eventPublisher ?? NullDeviceEventPublisher.Instance;
        _acquisitionArchive = acquisitionArchive ?? NullDeviceAcquisitionArchive.Instance;
        if (string.IsNullOrWhiteSpace(options.DeviceId))
            throw new ArgumentException("Device ID boş olamaz.", nameof(options));
        _sensor.StatusChanged += OnStatusChanged;
    }

    public string DeviceId => _options.DeviceId;

    public bool Matches(string deviceId) =>
        string.Equals(DeviceId, deviceId, StringComparison.OrdinalIgnoreCase);

    public DeviceStateContract GetState() => CreateState();

    public DeviceProtocolContract GetProtocol() => new(
        DeviceProtocolVersion.Current,
        "XRaySystem.DeviceHost",
        DeviceId,
        _options.IsSimulator,
        ["Snap", "Grab", "Freeze", "Abort"],
        ["Raw", "Low"]);

    public TelemetryEventContract CreateTelemetryEvent(XRayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var frame = measurement.Frame ?? throw new InvalidOperationException("Ölçüm frame içermiyor.");
        var requested = _sensor.Parameters;
        var readings = measurement.Readings;
        return new TelemetryEventContract(
            DeviceId,
            Interlocked.Increment(ref _sequenceNumber),
            _sensor.CurrentStatus.ToString(),
            frame.AcquisitionId,
            frame.Kind.ToString(),
            frame.Width,
            frame.Height,
            LowFrameAvailable: false,
            LowWidth: 0,
            LowHeight: 0,
            frame.Timestamp.ToUniversalTime(),
            frame.Settings.Profile.ToString(),
            new AcquisitionParametersContract(
                requested.VoltageKv,
                requested.PowerW,
                requested.ExposureTimeMs),
            new MeasuredTelemetryContract(
                readings.Voltage,
                readings.TubeCurrent,
                readings.Voltage * readings.TubeCurrent,
                readings.XRayIntensity,
                readings.ExposureTime,
                ToContract(readings.VoltageProvenance),
                ToContract(readings.TubeCurrentProvenance),
                ToContract(readings.ExposureTimeProvenance)),
            frame.Operation == AcquisitionOperation.Snap
                ? AcquisitionOperationContract.Snap
                : AcquisitionOperationContract.Grab);
    }

    public async Task<DeviceCommandResult> ConnectAsync(DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        return await ExecuteAsync(request.CommandId, async () =>
        {
            await _sensor.ConnectAsync().ConfigureAwait(false);
            return "Cihaz bağlandı.";
        }, "connect", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceCommandResult> DisconnectAsync(DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        if (_sensor.CurrentStatus == DeviceStatus.Snapping)
            await _sensor.AbortAsync().ConfigureAwait(false);
        else if (_sensor.CurrentStatus == DeviceStatus.Grabbing)
            await _sensor.StopCaptureAsync().ConfigureAwait(false);
        return await ExecuteAsync(request.CommandId, async () =>
        {
            await _sensor.DisconnectAsync().ConfigureAwait(false);
            return "Cihaz bağlantısı kesildi.";
        }, "disconnect", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceCommandResult> StartLiveAsync(DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        return await ExecuteAsync(request.CommandId, async () =>
        {
            await _sensor.StartCaptureAsync().ConfigureAwait(false);
            return "Canlı akış başlatıldı.";
        }, "live.start", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceCommandResult> StartGrabAsync(
        DeviceCommandRequest request,
        CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        return await ExecuteAsync(request.CommandId, async () =>
        {
            await _sensor.StartCaptureAsync().ConfigureAwait(false);
            return "GRAB başlatıldı.";
        }, "grab.start", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceCommandResult> StopLiveAsync(DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        return await ExecuteAsync(request.CommandId, async () =>
        {
            await _sensor.AbortAsync().ConfigureAwait(false);
            return "Canlı akış durduruldu.";
        }, "live.stop", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceCommandResult> FreezeGrabAsync(
        DeviceCommandRequest request,
        CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        return await ExecuteAsync(request.CommandId, async () =>
        {
            await _sensor.StopCaptureAsync().ConfigureAwait(false);
            return "GRAB donduruldu; son doğrulanmış kare korunuyor.";
        }, "grab.freeze", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceCommandResult> AbortAsync(
        DeviceCommandRequest request,
        CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        ThrowIfDisposed();
        var lockTaken = false;
        try
        {
            await _abortLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            if (TryGetCompletedCommand(
                    request.CommandId,
                    "acquisition.abort",
                    out DeviceCommandResult completed))
                return completed;

            await _sensor.StopCaptureAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _sequenceNumber);
            var result = new DeviceCommandResult(
                request.CommandId,
                true,
                "Aktif acquisition güvenli durdurma yoluna alındı.",
                CreateState());
            RememberCompletedCommand(request.CommandId, "acquisition.abort", result);
            return result;
        }
        catch (Exception ex)
        {
            await TryPublishErrorAsync(request.CommandId, "acquisition.abort", ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (lockTaken) _abortLock.Release();
        }
    }

    public async Task<DeviceCommandResult> UpdateParametersAsync(
        UpdateAcquisitionParametersRequest request,
        CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        return await ExecuteAsync(request.CommandId, () =>
        {
            var parameters = new AcquisitionParameters(
                request.VoltageKv,
                request.PowerW,
                request.ExposureTimeMs);
            parameters.EnsureValid();
            if (!Enum.TryParse<AcquisitionProfile>(request.Profile, ignoreCase: true, out var profile) ||
                !Enum.IsDefined(profile))
                throw new ArgumentException("Sensör profili geçersiz.", nameof(request.Profile));
            _sensor.Parameters = parameters;
            _sensor.Profile = profile;
            return Task.FromResult("Çekim parametreleri güncellendi.");
        }, "parameters", cancellationToken).ConfigureAwait(false);
    }

    public Task<SnapCommandResult> SnapAsync(
        DeviceCommandRequest request,
        CancellationToken cancellationToken) =>
        SnapAsync(
            new SnapCommandRequest(request.CommandId, ContractSnapFrameMode.Raw),
            cancellationToken);

    public async Task<SnapCommandResult> SnapAsync(
        SnapCommandRequest request,
        CancellationToken cancellationToken,
        string? initiatedBySubject = null,
        string? initiatedByClientId = null)
    {
        EnsureCommandId(request.CommandId);
        if (!Enum.IsDefined(request.FrameMode))
            throw new ArgumentOutOfRangeException(nameof(request.FrameMode));
        ThrowIfDisposed();
        var operationName = $"snap.{request.FrameMode.ToString().ToLowerInvariant()}";
        var lockTaken = false;
        var exposureCompleted = false;
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            if (TryGetCompletedCommand(request.CommandId, operationName, out SnapCommandResult completed))
                return completed;

            await TryPublishSnapProgressAsync(request.CommandId, "queued", 0).ConfigureAwait(false);
            await TryPublishSnapProgressAsync(request.CommandId, "acquiring", 20).ConfigureAwait(false);
            var requestedParameters = _sensor.Parameters;
            var frameMode = request.FrameMode == ContractSnapFrameMode.Low
                ? DomainSnapFrameMode.Low
                : DomainSnapFrameMode.Raw;
            var measurement = await _sensor
                .SnapAsync(
                    new SnapRequest(requestedParameters.ExposureTimeMs, frameMode),
                    cancellationToken)
                .ConfigureAwait(false);
            exposureCompleted = true;
            var frame = measurement.Frame ?? throw new InvalidOperationException("Sensör SNAP frame döndürmedi.");
            if (frame.Operation != AcquisitionOperation.Snap || frame.SnapFrameMode != frameMode)
                throw new InvalidDataException("Sensör SNAP frame'i istenen LOW/RAW moduyla uyuşmuyor.");
            Interlocked.Increment(ref _sequenceNumber);

            await TryPublishSnapProgressAsync(request.CommandId, "processing", 60).ConfigureAwait(false);
            var readings = measurement.Readings;
            var autoWindow = WindowLevelMapper.Auto(frame.Pixels);
            var lowPreview = Gray8BmpEncoder.EncodePreview(
                frame,
                maxWidth: 768,
                maxHeight: 768,
                autoWindow.Center,
                autoWindow.Width,
                CancellationToken.None);
            var image = new RemoteImageMetadataContract(
                measurement.Id,
                frame.AcquisitionId,
                frame.Kind.ToString(),
                frame.Width,
                frame.Height,
                frame.StrideBytes,
                frame.PixelFormat.ToString(),
                lowPreview.Width,
                lowPreview.Height,
                frame.Timestamp.ToUniversalTime(),
                frame.Settings.Profile.ToString(),
                new AcquisitionParametersContract(
                    requestedParameters.VoltageKv,
                    requestedParameters.PowerW,
                    requestedParameters.ExposureTimeMs),
                new MeasuredTelemetryContract(
                    readings.Voltage,
                    readings.TubeCurrent,
                    readings.Voltage * readings.TubeCurrent,
                    readings.XRayIntensity,
                    readings.ExposureTime,
                    ToContract(readings.VoltageProvenance),
                    ToContract(readings.TubeCurrentProvenance),
                    ToContract(readings.ExposureTimeProvenance)),
                LowAvailable: true,
                RawAvailable: true,
                AcquisitionOperationContract.Snap,
                request.FrameMode,
                IsSimulator: _options.IsSimulator,
                InitiatedBySubject: NormalizeInitiator(initiatedBySubject),
                InitiatedByClientId: NormalizeInitiator(initiatedByClientId));

            await TryPublishSnapProgressAsync(request.CommandId, "persisting", 75).ConfigureAwait(false);
            var archiveReceipt = _acquisitionArchive.Save(measurement, image, lowPreview.Bytes);
            image = image with { PersistedOnDevice = archiveReceipt.Persisted };
            await TryPublishSnapProgressAsync(request.CommandId, "persisted", 85).ConfigureAwait(false);

            await TryPublishSnapProgressAsync(request.CommandId, "buffering", 90).ConfigureAwait(false);
            _imageBuffer.Add(image, frame, lowPreview.Bytes);

            await TryPublishFrameReadyAsync(image).ConfigureAwait(false);

            await TryPublishSnapProgressAsync(request.CommandId, "completed", 100).ConfigureAwait(false);

            var result = new SnapCommandResult(
                request.CommandId,
                Succeeded: true,
                archiveReceipt.Persisted
                    ? "SNAP alındı, cihaz bilgisayarına kalıcı kaydedildi ve remote buffer'a eklendi."
                    : "SNAP alındı ve sınırlı remote buffer'a eklendi; cihaz arşivi devre dışı.",
                CreateState(),
                image);
            RememberCompletedCommand(request.CommandId, operationName, result);
            return result;
        }
        catch (Exception ex)
        {
            if (exposureCompleted)
                RememberFailedCommand(request.CommandId, operationName, ex);
            await TryPublishErrorAsync(request.CommandId, "snap", ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (lockTaken) _commandLock.Release();
        }
    }

    private static string? NormalizeInitiator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new ArgumentException("Acquisition initiator en fazla 128 görünür karakter olabilir.");
        return normalized;
    }

    private async Task<DeviceCommandResult> ExecuteAsync(
        Guid commandId,
        Func<Task<string>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var lockTaken = false;
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            if (TryGetCompletedCommand(commandId, operationName, out DeviceCommandResult completed))
                return completed;

            var message = await operation().ConfigureAwait(false);
            Interlocked.Increment(ref _sequenceNumber);
            var result = new DeviceCommandResult(commandId, true, message, CreateState());
            RememberCompletedCommand(commandId, operationName, result);
            return result;
        }
        catch (Exception ex)
        {
            await TryPublishErrorAsync(commandId, operationName, ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (lockTaken) _commandLock.Release();
        }
    }

    private bool TryGetCompletedCommand<TResult>(
        Guid commandId,
        string operation,
        out TResult result)
        where TResult : class
    {
        lock (_completedCommandsGate)
        {
            if (!_completedCommands.TryGetValue(commandId, out var completed))
            {
                result = null!;
                return false;
            }

            if (!string.Equals(completed.Operation, operation, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Aynı commandId farklı bir işlem için tekrar kullanılamaz.");

            if (completed.Result is CachedCommandFailure failure)
                ExceptionDispatchInfo.Capture(failure.Exception).Throw();

            result = completed.Result as TResult
                ?? throw new InvalidOperationException("Önbellekteki komut sonucu beklenen türle uyuşmuyor.");
            return true;
        }
    }

    private void RememberFailedCommand(Guid commandId, string operation, Exception exception)
    {
        lock (_completedCommandsGate)
        {
            if (_completedCommands.ContainsKey(commandId)) return;
            _completedCommands.Add(commandId, new CompletedCommand(operation, new CachedCommandFailure(exception)));
            _completedCommandOrder.Enqueue(commandId);
            while (_completedCommandOrder.Count > CompletedCommandLimit)
                _completedCommands.Remove(_completedCommandOrder.Dequeue());
        }
    }

    private void RememberCompletedCommand(Guid commandId, string operation, object result)
    {
        lock (_completedCommandsGate)
        {
            _completedCommands.Add(commandId, new CompletedCommand(operation, result));
            _completedCommandOrder.Enqueue(commandId);
            while (_completedCommandOrder.Count > CompletedCommandLimit)
                _completedCommands.Remove(_completedCommandOrder.Dequeue());
        }
    }

    private async Task TryPublishSnapProgressAsync(Guid commandId, string stage, int percent)
    {
        try
        {
            await _eventPublisher.PublishSnapProgressAsync(
                new SnapProgressEventContract(
                    DeviceId,
                    commandId,
                    stage,
                    percent,
                    DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Event delivery must not make a device command fail.
        }
    }

    private async Task TryPublishFrameReadyAsync(RemoteImageMetadataContract image)
    {
        try
        {
            await _eventPublisher.PublishFrameReadyAsync(
                new FrameReadyEventContract(
                    DeviceId,
                    Interlocked.Increment(ref _sequenceNumber),
                    image.AcquisitionId,
                    image.Operation,
                    image.Kind,
                    image.Width,
                    image.Height,
                    image.CapturedAtUtc,
                    image.ImageId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Event delivery must not make an already persisted SNAP fail.
        }
    }

    private async Task TryPublishErrorAsync(Guid commandId, string operation, Exception exception)
    {
        var code = exception switch
        {
            AcquisitionTimeoutException => "acquisition_timeout",
            AcquisitionPersistenceException => "persistence_failed",
            InvalidDataException => "invalid_frame",
            OperationCanceledException => "command_cancelled",
            ArgumentException => "invalid_argument",
            InvalidOperationException => "invalid_operation",
            _ => "command_failed"
        };

        try
        {
            await _eventPublisher.PublishErrorAsync(
                new DeviceErrorEventContract(
                    DeviceId,
                    commandId,
                    operation,
                    code,
                    exception.Message,
                    DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Event delivery must not hide the original command error.
        }
    }

    private DeviceStateContract CreateState()
    {
        var parameters = _sensor.Parameters;
        var hardware = (_sensor as IAcquisitionCoordinator)?.HardwareState;
        return new DeviceStateContract(
            DeviceId,
            _sensor.CurrentStatus.ToString(),
            _options.IsSimulator,
            new AcquisitionParametersContract(
                parameters.VoltageKv,
                parameters.PowerW,
                parameters.ExposureTimeMs),
            Volatile.Read(ref _sequenceNumber),
            DateTimeOffset.UtcNow,
            _sensor.Profile.ToString(),
            hardware?.CoordinatorState.ToString(),
            hardware?.GeneratorConnected,
            hardware?.DetectorConnected,
            hardware?.XRayActive,
            hardware?.DetectorAcquiring,
            hardware?.ActiveAcquisitionId);
    }

    private static MeasurementProvenanceContract ToContract(MeasurementProvenance provenance) =>
        provenance switch
        {
            MeasurementProvenance.Measured => MeasurementProvenanceContract.Measured,
            MeasurementProvenance.AppliedTarget => MeasurementProvenanceContract.AppliedTarget,
            MeasurementProvenance.Derived => MeasurementProvenanceContract.Derived,
            _ => MeasurementProvenanceContract.Unknown
        };

    private void OnStatusChanged(DeviceStatus _) => Interlocked.Increment(ref _sequenceNumber);

    private static void EnsureCommandId(Guid commandId)
    {
        if (commandId == Guid.Empty)
            throw new ArgumentException("CommandId boş olamaz.", nameof(commandId));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sensor.StatusChanged -= OnStatusChanged;
        _commandLock.Dispose();
        _abortLock.Dispose();
    }
}
