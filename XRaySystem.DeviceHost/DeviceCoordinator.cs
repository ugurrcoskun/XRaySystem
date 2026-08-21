using XRaySystem.Contracts;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Domain.WindowLevel;
using XRaySystem.Infrastructure.Imaging;

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
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly Dictionary<Guid, CompletedCommand> _completedCommands = [];
    private readonly Queue<Guid> _completedCommandOrder = [];
    private long _sequenceNumber;
    private bool _disposed;

    private sealed record CompletedCommand(string Operation, object Result);

    public DeviceCoordinator(
        ISensorService sensor,
        DeviceHostOptions options,
        RemoteImageBuffer imageBuffer,
        IDeviceEventPublisher? eventPublisher = null)
    {
        _sensor = sensor;
        _options = options;
        _imageBuffer = imageBuffer;
        _eventPublisher = eventPublisher ?? NullDeviceEventPublisher.Instance;
        if (string.IsNullOrWhiteSpace(options.DeviceId))
            throw new ArgumentException("Device ID boş olamaz.", nameof(options));
        _sensor.StatusChanged += OnStatusChanged;
    }

    public string DeviceId => _options.DeviceId;

    public bool Matches(string deviceId) =>
        string.Equals(DeviceId, deviceId, StringComparison.OrdinalIgnoreCase);

    public DeviceStateContract GetState() => CreateState();

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
                readings.ExposureTime));
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

    public async Task<DeviceCommandResult> StopLiveAsync(DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        return await ExecuteAsync(request.CommandId, async () =>
        {
            await _sensor.StopCaptureAsync().ConfigureAwait(false);
            return "Canlı akış durduruldu.";
        }, "live.stop", cancellationToken).ConfigureAwait(false);
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

    public async Task<SnapCommandResult> SnapAsync(DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        EnsureCommandId(request.CommandId);
        ThrowIfDisposed();
        var lockTaken = false;
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            if (TryGetCompletedCommand(request.CommandId, "snap", out SnapCommandResult completed))
                return completed;

            await TryPublishSnapProgressAsync(request.CommandId, "queued", 0).ConfigureAwait(false);
            await TryPublishSnapProgressAsync(request.CommandId, "acquiring", 20).ConfigureAwait(false);
            var requestedParameters = _sensor.Parameters;
            var measurement = await _sensor
                .SnapAsync(requestedParameters.ExposureTimeMs, cancellationToken)
                .ConfigureAwait(false);
            var frame = measurement.Frame ?? throw new InvalidOperationException("Sensör SNAP frame döndürmedi.");
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
                cancellationToken);
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
                    readings.ExposureTime),
                LowAvailable: true,
                RawAvailable: true);
            await TryPublishSnapProgressAsync(request.CommandId, "buffering", 85).ConfigureAwait(false);
            _imageBuffer.Add(image, frame, lowPreview.Bytes);

            await TryPublishSnapProgressAsync(request.CommandId, "completed", 100).ConfigureAwait(false);

            var result = new SnapCommandResult(
                request.CommandId,
                Succeeded: true,
                "SNAP alındı ve sınırlı remote buffer'a eklendi.",
                CreateState(),
                image);
            RememberCompletedCommand(request.CommandId, "snap", result);
            return result;
        }
        catch (Exception ex)
        {
            await TryPublishErrorAsync(request.CommandId, "snap", ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (lockTaken) _commandLock.Release();
        }
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
        if (!_completedCommands.TryGetValue(commandId, out var completed))
        {
            result = null!;
            return false;
        }

        if (!string.Equals(completed.Operation, operation, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Aynı commandId farklı bir işlem için tekrar kullanılamaz.");

        result = completed.Result as TResult
            ?? throw new InvalidOperationException("Önbellekteki komut sonucu beklenen türle uyuşmuyor.");
        return true;
    }

    private void RememberCompletedCommand(Guid commandId, string operation, object result)
    {
        _completedCommands.Add(commandId, new CompletedCommand(operation, result));
        _completedCommandOrder.Enqueue(commandId);
        while (_completedCommandOrder.Count > CompletedCommandLimit)
            _completedCommands.Remove(_completedCommandOrder.Dequeue());
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

    private async Task TryPublishErrorAsync(Guid commandId, string operation, Exception exception)
    {
        var code = exception switch
        {
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
            _sensor.Profile.ToString());
    }

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
    }
}
