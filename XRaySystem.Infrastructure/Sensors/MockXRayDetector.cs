using System.Collections.Concurrent;
using System.Threading.Channels;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Sensors;

/// <summary>
/// Detector-boundary simulator with explicit notification, buffer-copy and
/// release steps. LOW and RAW are different source acquisitions.
/// </summary>
public sealed class MockXRayDetector : IXRayDetector, IDisposable
{
    private const int LowWidth = 512;
    private const int LowHeight = 512;
    private const int RawWidth = 2304;
    private const int RawHeight = 2930;

    private readonly object _gate = new();
    private readonly Channel<DetectorFrameToken> _frames = Channel.CreateBounded<DetectorFrameToken>(
        new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly ConcurrentDictionary<Guid, SnapFrameMode> _bufferModes = new();
    private CancellationTokenSource? _grabCancellation;
    private Task? _grabProducer;
    private Guid _configuredAcquisitionId;
    private SnapFrameMode _configuredMode = SnapFrameMode.Low;
    private double _exposureTimeMs = 100;
    private bool _isConnected;
    private bool _isArmed;
    private bool _isAcquiring;
    private long _sequence;
    private bool _disposed;

    public bool IsConnected { get { lock (_gate) return _isConnected; } }
    public bool IsAcquiring { get { lock (_gate) return _isAcquiring; } }

    public bool SupportsSnapMode(SnapFrameMode frameMode) => Enum.IsDefined(frameMode);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _isConnected = true;
        }
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _isArmed = false;
            _isAcquiring = false;
            _isConnected = false;
        }
    }

    public Task ConfigureSnapAsync(
        Guid acquisitionId,
        DetectorSnapSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EnsureValid();
        if (!SupportsSnapMode(settings.FrameMode))
            throw new NotSupportedException($"SNAP {settings.FrameMode} desteklenmiyor.");
        Configure(acquisitionId, settings.FrameMode, settings.ExposureTimeMs, cancellationToken);
        return Task.CompletedTask;
    }

    public Task ConfigureGrabAsync(
        Guid grabSessionId,
        DetectorGrabSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EnsureValid();
        Configure(grabSessionId, SnapFrameMode.Low, settings.ExposureTimeMs, cancellationToken);
        return Task.CompletedTask;
    }

    public Task ArmAsync(Guid acquisitionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureConfigured(acquisitionId);
            _isArmed = true;
        }
        return Task.CompletedTask;
    }

    public Task WaitUntilArmedAsync(Guid acquisitionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureConfigured(acquisitionId);
            if (!_isArmed)
                throw new InvalidOperationException("Detector armed değil.");
        }
        return Task.CompletedTask;
    }

    public Task StartSnapAsync(Guid acquisitionId, CancellationToken cancellationToken)
    {
        SnapFrameMode mode;
        lock (_gate)
        {
            EnsureArmed(acquisitionId);
            mode = _configuredMode;
            _isAcquiring = true;
        }

        return ProduceFrameAsync(acquisitionId, mode, cancellationToken).AsTask();
    }

    public Task StartGrabAsync(Guid grabSessionId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureArmed(grabSessionId);
            if (_grabProducer is { IsCompleted: false })
                throw new InvalidOperationException("Detector GRAB zaten aktif.");
            _grabCancellation?.Dispose();
            _grabCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _isAcquiring = true;
            _grabProducer = ProduceGrabAsync(grabSessionId, _grabCancellation.Token);
        }
        return Task.CompletedTask;
    }

    public async Task FreezeAsync(Guid grabSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? producer;
        lock (_gate)
        {
            if (_configuredAcquisitionId != Guid.Empty && _configuredAcquisitionId != grabSessionId)
                return;
            _grabCancellation?.Cancel();
            _isAcquiring = false;
            producer = _grabProducer;
        }
        if (producer is not null)
        {
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? producer;
        lock (_gate)
        {
            _grabCancellation?.Cancel();
            producer = _grabProducer;
            _isArmed = false;
            _isAcquiring = false;
        }
        if (producer is not null)
        {
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        while (_frames.Reader.TryRead(out var staleToken))
            _bufferModes.TryRemove(staleToken.BufferTokenId, out _);
    }

    public Task<DetectorFrameToken> WaitForFrameAsync(CancellationToken cancellationToken) =>
        _frames.Reader.ReadAsync(cancellationToken).AsTask();

    public Task<ushort[]> CopyFrameAsync(DetectorFrameToken token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(token);
        if (!_bufferModes.TryGetValue(token.BufferTokenId, out var mode))
            throw new InvalidOperationException("Detector buffer token geçerli değil veya daha önce release edildi.");

        var pixels = new ushort[checked(token.Width * token.Height)];
        var modeOffset = mode == SnapFrameMode.Raw ? 2048 : 512;
        for (var y = 0; y < token.Height; y++)
        {
            var row = y * token.Width;
            for (var x = 0; x < token.Width; x++)
                pixels[row + x] = (ushort)((modeOffset + x * 11 + y * 7) & ushort.MaxValue);
        }
        return Task.FromResult(pixels);
    }

    public Task ReleaseFrameAsync(DetectorFrameToken token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _bufferModes.TryRemove(token.BufferTokenId, out _);
        return Task.CompletedTask;
    }

    private void Configure(
        Guid acquisitionId,
        SnapFrameMode mode,
        double exposureTimeMs,
        CancellationToken cancellationToken)
    {
        if (acquisitionId == Guid.Empty)
            throw new ArgumentException("Acquisition ID boş olamaz.", nameof(acquisitionId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureConnected();
            _configuredAcquisitionId = acquisitionId;
            _configuredMode = mode;
            _exposureTimeMs = exposureTimeMs;
            _isArmed = false;
        }
    }

    private async Task ProduceGrabAsync(Guid grabSessionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProduceFrameAsync(grabSessionId, SnapFrameMode.Low, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(_exposureTimeMs, 33, 250)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask ProduceFrameAsync(
        Guid acquisitionId,
        SnapFrameMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = mode == SnapFrameMode.Raw ? RawWidth : LowWidth;
        var height = mode == SnapFrameMode.Raw ? RawHeight : LowHeight;
        var token = new DetectorFrameToken(
            Guid.NewGuid(),
            acquisitionId,
            Interlocked.Increment(ref _sequence),
            width,
            height,
            checked(width * sizeof(ushort)),
            Gray16PixelFormat.Gray16LittleEndian,
            $"MockXRayDetector:{mode}");
        _bufferModes[token.BufferTokenId] = mode;
        try
        {
            await _frames.Writer.WriteAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _bufferModes.TryRemove(token.BufferTokenId, out _);
            throw;
        }
    }

    private static void ValidateToken(DetectorFrameToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.BufferTokenId == Guid.Empty || token.AcquisitionId == Guid.Empty)
            throw new InvalidDataException("Detector token kimliği geçersiz.");
        if (token.Width <= 0 || token.Height <= 0)
            throw new InvalidDataException("Detector frame boyutları geçersiz.");
        if (token.StrideBytes != checked(token.Width * sizeof(ushort)))
            throw new InvalidDataException("Detector frame stride canonical Gray16 ile uyuşmuyor.");
    }

    private void EnsureConfigured(Guid acquisitionId)
    {
        EnsureConnected();
        if (_configuredAcquisitionId != acquisitionId)
            throw new InvalidOperationException("Detector farklı bir acquisition için yapılandırıldı.");
    }

    private void EnsureArmed(Guid acquisitionId)
    {
        EnsureConfigured(acquisitionId);
        if (!_isArmed)
            throw new InvalidOperationException("Detector armed değil.");
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isConnected)
            throw new InvalidOperationException("Detector bağlı değil.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _grabCancellation?.Cancel();
        _grabCancellation?.Dispose();
    }
}
