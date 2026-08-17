using System;
using System.Threading;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Sensors;

/// <summary>
/// ISensorService implementasyonu: 16-bit Sentetik Dijital Test Fantomu üreticisi.
/// Dış dosyaya ihtiyaç duymadan RAM üzerinde Step-Wedge (Adım Takozu),
/// Çözünürlük Çizgi Çiftleri, Test Daireleri ve kV/mA'ya bağlı gürültü üretir.
/// </summary>
public sealed class MockXRaySensorService : ISensorService, IDisposable
{
    private DeviceStatus _currentStatus = DeviceStatus.Disconnected;
    private CancellationTokenSource? _captureTokenSource;
    private Task? _producerTask;
    private Task? _producerStopTask;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _snapLock = new(1, 1);
    private CancellationTokenSource _sessionCancellation = new();
    private CancellationTokenSource? _snapCancellation;
    private TaskCompletionSource<bool> _allSnapsCompletion = CompletedTcs();
    private readonly Random _random = new();
    private volatile bool _disconnectRequested;
    private int _stopTransitionCount;
    private readonly object _disposeLock = new();
    private bool _disposeRequested;
    private bool _disposed;
    private int _activeSnapCount;
    private int _producerGeneration;
    private int _operationEpoch;
    private Task? _disconnectTask;
    private int _activeLifecycleOperations;
    private TaskCompletionSource<bool>? _lifecycleOperationsCompleted;

    private bool IsStopTransitionRequested => Volatile.Read(ref _stopTransitionCount) != 0;

    private const int SnapWidth = 2304;
    private const int SnapHeight = 2930;
    private const int LiveWidth = 512;
    private const int LiveHeight = 512;

    public DeviceStatus CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (_currentStatus != value)
            {
                _currentStatus = value;
                StatusChanged?.Invoke(_currentStatus);
            }
        }
    }

    public event Action<DeviceStatus>? StatusChanged;
    public event Action<XRayMeasurement>? MeasurementReceived;
    public AcquisitionProfile Profile { get; set; } = AcquisitionProfile.IndustrialQaPhantom;

    public double TargetVoltage { get; set; } = 70.0;
    public double TargetTubeCurrent { get; set; } = 50.0;

    public async Task ConnectAsync()
    {
        var lifecycle = await EnterLifecycleAsync().ConfigureAwait(false);
        try
        {
            if (CurrentStatus != DeviceStatus.Disconnected) return;

            lock (_disposeLock)
            {
                if (_disposeRequested || _disposed)
                    throw new ObjectDisposedException(nameof(MockXRaySensorService));
                if (_disconnectTask is { IsCompleted: true })
                    _disconnectTask = null;
            }

            _disconnectRequested = false;
            _sessionCancellation.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            CurrentStatus = DeviceStatus.Connected;
        }
        finally { lifecycle.Release(); }
    }

    public async Task DisconnectAsync()
    {
        Task disconnectTask;
        lock (_disposeLock)
        {
            if (_disconnectTask != null) { disconnectTask = _disconnectTask; }
            else if (_disposed) { return; }
            else
            {
                _disconnectRequested = true;
                Interlocked.Increment(ref _operationEpoch);
                disconnectTask = DisconnectCoreAsync();
                _disconnectTask = disconnectTask;
            }
        }
        await disconnectTask.ConfigureAwait(false);
    }

    private async Task DisconnectCoreAsync()
    {
        var lifecycle = await EnterLifecycleAsync(allowDispose: true).ConfigureAwait(false);
        Task snapCompletion;
        try
        {
            _sessionCancellation.Cancel();
            _snapCancellation?.Cancel();
            if (CurrentStatus != DeviceStatus.Disconnected)
            {
                var producerStop = StopProducerUnderLock();
                lifecycle.Release();
                await producerStop.ConfigureAwait(false);
                lifecycle = await EnterLifecycleAsync(allowDispose: true).ConfigureAwait(false);
                CurrentStatus = DeviceStatus.Disconnected;
            }
            snapCompletion = _allSnapsCompletion.Task;
        }
        finally { lifecycle.Release(); }

        await snapCompletion.ConfigureAwait(false);
    }

    public async Task StartCaptureAsync()
    {
        var lifecycle = await EnterLifecycleAsync().ConfigureAwait(false);
        try
        {
            if (CurrentStatus != DeviceStatus.Connected && CurrentStatus != DeviceStatus.Frozen)
                throw new InvalidOperationException("Cihaz bağlı değil. Önce ConnectAsync çağrılmalıdır.");
            if (_disconnectRequested || IsStopTransitionRequested)
                throw new InvalidOperationException("Cihaz yaşam döngüsü geçişi devam ediyor.");
            if (_producerStopTask is { IsCompleted: false }) return;
            if (_producerTask is { IsCompleted: false }) return;

            _sessionCancellation.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            _captureTokenSource?.Dispose();
            _captureTokenSource = new CancellationTokenSource();
            CurrentStatus = DeviceStatus.Capturing;
            var generation = ++_producerGeneration;
            _producerTask = LiveProducerLoopAsync(_captureTokenSource.Token, generation);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopCaptureAsync()
    {
        Interlocked.Increment(ref _stopTransitionCount);
        Interlocked.Increment(ref _operationEpoch);
        var lifecycle = await EnterLifecycleAsync().ConfigureAwait(false);
        Task snapCompletion;
        try
        {
            if (CurrentStatus == DeviceStatus.Disconnected)
            {
                snapCompletion = _allSnapsCompletion.Task;
                goto AwaitSnap;
            }
            _sessionCancellation.Cancel();
            _snapCancellation?.Cancel();
            var producerStop = StopProducerUnderLock();
            lifecycle.Release();
            await producerStop.ConfigureAwait(false);
            lifecycle = await EnterLifecycleAsync().ConfigureAwait(false);
            if (CurrentStatus != DeviceStatus.Snapping && CurrentStatus != DeviceStatus.Frozen)
                CurrentStatus = DeviceStatus.Connected;
            snapCompletion = _allSnapsCompletion.Task;
        }
        finally
        {
            lifecycle.Release();
            Interlocked.Decrement(ref _stopTransitionCount);
        }

    AwaitSnap:
        await snapCompletion;
    }

    public async Task<XRayMeasurement> SnapAsync(double exposureTime, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(exposureTime) || exposureTime < 1 || exposureTime > 3000)
            throw new ArgumentOutOfRangeException(nameof(exposureTime));

        var lifecycle = await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
        var snapGeneration = 0;
        var snapRegistered = false;
        try
        {
            lock (_disposeLock)
                if (_disposeRequested || _disposed) throw new ObjectDisposedException(nameof(MockXRaySensorService));
            if (_disconnectRequested || IsStopTransitionRequested || CurrentStatus == DeviceStatus.Disconnected)
                throw new InvalidOperationException("Cihaz bağlı değil.");
            snapGeneration = _operationEpoch;
            if (_activeSnapCount++ == 0)
                _allSnapsCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            snapRegistered = true;
        }
        finally { lifecycle.Release(); }

        var snapLockAcquired = false;
        CancellationTokenSource? snapCts = null;
        try
        {
            await _snapLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            snapLockAcquired = true;

            lifecycle = await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disconnectRequested || IsStopTransitionRequested || CurrentStatus == DeviceStatus.Disconnected ||
                    CurrentStatus == DeviceStatus.Snapping || snapGeneration != _operationEpoch)
                    throw new InvalidOperationException("Cihaz bağlı değil.");
                CurrentStatus = DeviceStatus.Snapping;
                _sessionCancellation.Cancel();
                var producerStop = StopProducerUnderLock();
                lifecycle.Release();
                lifecycle = null!;
                await producerStop.ConfigureAwait(false);
                lifecycle = await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
                if (_disconnectRequested || IsStopTransitionRequested || CurrentStatus == DeviceStatus.Disconnected ||
                    snapGeneration != _operationEpoch)
                    throw new OperationCanceledException("Snap was stopped by a lifecycle transition.");
                _sessionCancellation.Dispose();
                _sessionCancellation = new CancellationTokenSource();
                snapCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCancellation.Token);
                _snapCancellation = snapCts;
            }
            finally { lifecycle?.Release(); }

            // Exposure süresini simüle et
            await Task.Delay((int)exposureTime, snapCts.Token).ConfigureAwait(false);

            double actualVoltage = Math.Round(TargetVoltage + (_random.NextDouble() - 0.5) * 0.4, 1);
            double actualCurrent = Math.Round(TargetTubeCurrent + (_random.NextDouble() - 0.5) * 0.2, 2);
            int intensity = (int)(actualVoltage * actualCurrent * 0.75 + _random.Next(-50, 50));

            var readings = new SensorReadings(
                TubeCurrent: actualCurrent,
                Voltage: actualVoltage,
                XRayIntensity: Math.Max(100, intensity),
                ExposureTime: exposureTime
            );

            // Matematiksel Dijital Test Fantomu Üret (2304 x 2930, 16-bit)
            var pixels = GenerateDigitalPhantomPixels(SnapWidth, SnapHeight, readings);

            var frame = new RawGray16Frame(
                pixels, SnapWidth, SnapHeight, SnapWidth * 2,
                Gray16PixelFormat.Gray16LittleEndian,
                FrameKind.Snap,
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                new AcquisitionSettings(readings.TubeCurrent, readings.Voltage, exposureTime, Profile),
                "MockXRaySensorService",
                SnapWidth * 2, "little", "top-left");

            CurrentStatus = DeviceStatus.Frozen;
            return new XRayMeasurement(readings, frame, isSimulator: true);
        }
        finally
        {
            var cleanupLifecycle = await EnterLifecycleAsync(allowDispose: true).ConfigureAwait(false);
            try
            {
                if (CurrentStatus == DeviceStatus.Snapping && !_disconnectRequested)
                    CurrentStatus = DeviceStatus.Connected;
                if (ReferenceEquals(_snapCancellation, snapCts))
                    _snapCancellation = null;
            }
            finally { cleanupLifecycle.Release(); }

            if (snapLockAcquired) _snapLock.Release();
            snapCts?.Dispose();

            if (snapRegistered)
            {
                var countLifecycle = await EnterLifecycleAsync(allowDispose: true).ConfigureAwait(false);
                try
                {
                    if (--_activeSnapCount == 0)
                        _allSnapsCompletion.TrySetResult(true);
                }
                finally { countLifecycle.Release(); }
            }
        }
    }

    public Task ResumeLiveAsync()
    {
        if (_disconnectRequested) return Task.CompletedTask;
        return CurrentStatus is DeviceStatus.Connected or DeviceStatus.Frozen
            ? StartCaptureAsync() : Task.CompletedTask;
    }

    // ── Live Preview Loop ─────────────────────────────────────────────────────
    private async Task LiveProducerLoopAsync(CancellationToken token, int generation)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                double liveExposure = 33.0;
                double actualVoltage = Math.Round(TargetVoltage + (_random.NextDouble() - 0.5) * 0.6, 1);
                double actualCurrent = Math.Round(TargetTubeCurrent + (_random.NextDouble() - 0.5) * 0.3, 2);
                int intensity = (int)(actualVoltage * actualCurrent * 0.35 + _random.Next(-30, 30));

                var readings = new SensorReadings(
                    TubeCurrent: actualCurrent,
                    Voltage: actualVoltage,
                    XRayIntensity: Math.Max(50, intensity),
                    ExposureTime: liveExposure
                );

                var livePixels = GenerateDigitalPhantomPixels(LiveWidth, LiveHeight, readings);

                var frame = new RawGray16Frame(
                    livePixels, LiveWidth, LiveHeight, LiveWidth * 2,
                    Gray16PixelFormat.Gray16LittleEndian,
                    FrameKind.LivePreview,
                    DateTimeOffset.UtcNow,
                    Guid.NewGuid(),
                    new AcquisitionSettings(readings.TubeCurrent, readings.Voltage, liveExposure, Profile),
                    "MockXRaySensorService",
                    LiveWidth * 2, "little", "top-left");

                var measurement = new XRayMeasurement(readings, frame, isSimulator: true);
                if (token.IsCancellationRequested || generation != Volatile.Read(ref _producerGeneration))
                    break;
                MeasurementReceived?.Invoke(measurement);

                // 2 FPS
                await Task.Delay(500, token);
            }
        }
        catch (TaskCanceledException) { }
    }

    // ── Matematiksel Fantom Üreteci (Step-Wedge, Çözünürlük Çizgileri, Daireler) ──
    private ushort[] GenerateDigitalPhantomPixels(int width, int height, SensorReadings readings)
    {
        var pixels = new ushort[width * height];
        double voltageFactor = readings.Voltage / 100.0;
        double currentFactor = readings.TubeCurrent / 50.0;

        int centerX = width / 2;
        int centerY = height / 2;

        int numSteps = 7;
        int stepWidth = width / numSteps;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            double dy = y - centerY;

            for (int x = 0; x < width; x++)
            {
                double dx = x - centerX;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                // 1. Taban Gri Seviyesi (Step-Wedge / Adım Takozu)
                int stepIndex = Math.Clamp(x / stepWidth, 0, numSteps - 1);
                double baseGray = 5000 + (stepIndex * 7500) * voltageFactor * currentFactor;

                // 2. Konsantrik Test Daireleri
                if (dist < width * 0.40)
                {
                    double ring = Math.Sin(dist / 20.0);
                    baseGray += ring * 2500;
                }

                // 3. Çözünürlük Çizgi Çiftleri (Resolution Line Pairs)
                if (x > width * 0.15 && x < width * 0.35 && y > height * 0.15 && y < height * 0.45)
                {
                    int linePattern = (x / 4) % 2;
                    baseGray += (linePattern == 0) ? 6000 : -6000;
                }

                // 4. Rastgele X-Ray Poisson Gürültüsü
                double noise = (_random.NextDouble() - 0.5) * 800;
                baseGray += noise;

                pixels[rowOffset + x] = (ushort)Math.Clamp(baseGray, 0, 65535);
            }
        }

        return pixels;
    }

    // ── Lifecycle Helper ──────────────────────────────────────────────────────
    private static TaskCompletionSource<bool> CompletedTcs()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }

    private async Task<LifecycleLease> EnterLifecycleAsync(CancellationToken cancellationToken = default, bool allowDispose = false)
    {
        lock (_disposeLock)
        {
            if (!allowDispose && (_disposeRequested || _disposed))
                throw new ObjectDisposedException(nameof(MockXRaySensorService));
            ++_activeLifecycleOperations;
        }

        try
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new LifecycleLease(this);
        }
        catch
        {
            CompleteLifecycleOperation();
            throw;
        }
    }

    private void ReleaseLifecycle()
    {
        _lifecycleLock.Release();
        CompleteLifecycleOperation();
    }

    private void CompleteLifecycleOperation()
    {
        lock (_disposeLock)
        {
            if (--_activeLifecycleOperations == 0)
                _lifecycleOperationsCompleted?.TrySetResult(true);
        }
    }

    private sealed class LifecycleLease
    {
        private readonly MockXRaySensorService _owner;
        private int _released;

        public LifecycleLease(MockXRaySensorService owner) => _owner = owner;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.ReleaseLifecycle();
        }
    }

    private Task StopProducerUnderLock()
    {
        if (_producerStopTask is { IsCompleted: false })
            return _producerStopTask;

        var producer = _producerTask;
        var captureTokenSource = _captureTokenSource;
        _producerTask = null;
        _captureTokenSource = null;
        ++_producerGeneration;
        if (captureTokenSource == null)
            return _producerStopTask = producer == null ? Task.CompletedTask : AwaitProducerAsync(producer, null);

        captureTokenSource.Cancel();
        return _producerStopTask = AwaitProducerAsync(producer, captureTokenSource);
    }

    private static async Task AwaitProducerAsync(Task? producer, CancellationTokenSource? captureTokenSource)
    {
        try
        {
            if (producer != null)
                try { await producer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        finally
        {
            captureTokenSource?.Dispose();
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────
    public void Dispose()
    {
        Task disconnectTask;
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposeRequested = true;
            if (_disconnectTask == null)
            {
                _disconnectRequested = true;
                Interlocked.Increment(ref _operationEpoch);
                _disconnectTask = DisconnectCoreAsync();
            }
            disconnectTask = _disconnectTask;
        }
        disconnectTask.GetAwaiter().GetResult();
        Task lifecycleOperations;
        lock (_disposeLock)
        {
            lifecycleOperations = _activeLifecycleOperations == 0
                ? Task.CompletedTask
                : (_lifecycleOperationsCompleted ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        lifecycleOperations.GetAwaiter().GetResult();
        lock (_disposeLock)
        {
            if (_disposed) return;
            _captureTokenSource?.Dispose();
            _sessionCancellation.Dispose();
            _snapLock.Dispose();
            _lifecycleLock.Dispose();
            _disposed = true;
        }
    }
}
