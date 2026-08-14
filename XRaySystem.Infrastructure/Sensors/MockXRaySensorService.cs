using System;
using System.Threading;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Sensors;

public class MockXRaySensorService : ISensorService, IDisposable
{
    private DeviceStatus _currentStatus = DeviceStatus.Disconnected;
    private CancellationTokenSource? _captureTokenSource;
    private Task? _producerTask;
    private Task? _producerStopTask;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _snapLock = new(1, 1);
    private CancellationTokenSource _sessionCancellation = new();
    private CancellationTokenSource? _snapCancellation;
    private TaskCompletionSource<bool>? _snapCompletion;
    private TaskCompletionSource<bool> _allSnapsCompletion = CompletedTaskSource();
    private readonly Random _random = new();
    private volatile bool _disconnectRequested;
    private int _stopTransitionCount;
    private readonly object _disposeSync = new();
    private bool _disposeRequested;
    private bool _disposed;
    private int _activeSnapCount;
    private int _producerGeneration;
    private int _operationEpoch;
    private Task? _disconnectTask;
    private int _activeLifecycleOperations;
    private TaskCompletionSource<bool>? _lifecycleOperationsCompleted;

    private bool IsStopTransitionRequested => Volatile.Read(ref _stopTransitionCount) != 0;

    private static TaskCompletionSource<bool> CompletedTaskSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    // Simülasyon çözünürlüğü
    public const int Width = 2304;
    public const int Height = 2930;

    // Canlı önizleme çözünürlüğü – UI thread'i boğmaması için küçük tutuyoruz
    public const int LiveWidth  = 512;
    public const int LiveHeight = 512;

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

    public async Task ConnectAsync()
    {
        var lifecycle = await EnterLifecycleAsync().ConfigureAwait(false);
        try
        {
            if (CurrentStatus != DeviceStatus.Disconnected) return;
            await Task.Delay(500).ConfigureAwait(false);
            lock (_disposeSync)
            {
                if (_disposeRequested || _disposed)
                    throw new ObjectDisposedException(nameof(MockXRaySensorService));
                // A completed disconnect belongs to the old session. Keep a
                // new connection able to create its own disconnect operation.
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
        lock (_disposeSync)
        {
            if (_disconnectTask != null)
            {
                disconnectTask = _disconnectTask;
            }
            else if (_disposed)
            {
                return;
            }
            else
            {
                _disconnectRequested = true;
                Interlocked.Increment(ref _operationEpoch);
                disconnectTask = DisconnectCoreAsync(allowDispose: false);
                _disconnectTask = disconnectTask;
            }
        }

        await disconnectTask.ConfigureAwait(false);
    }

    private async Task DisconnectCoreAsync(bool allowDispose)
    {
        var lifecycle = await EnterLifecycleAsync(allowDispose: allowDispose).ConfigureAwait(false);
        Task snapCompletion;
        try
        {
            _sessionCancellation.Cancel();
            CancelSnapUnderLock();
            if (CurrentStatus != DeviceStatus.Disconnected)
            {
                var producerStop = StopProducerUnderLock();
                lifecycle.Release();
                await producerStop.ConfigureAwait(false);
                // Dispose may be requested while the producer is stopping, but
                // that must not invalidate this already-admitted transition.
                lifecycle = await EnterLifecycleAsync(allowDispose: true).ConfigureAwait(false);
                CurrentStatus = DeviceStatus.Disconnected;
            }
            // Do not dispose any synchronization primitive until every Snap has
            // completed its finally block and released _snapLock.
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
            if (_producerStopTask is { IsCompleted: false }) return;
            if (_producerTask is { IsCompleted: false }) return;
            _sessionCancellation.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            _captureTokenSource?.Dispose();
            _captureTokenSource = new CancellationTokenSource();
            CurrentStatus = DeviceStatus.Capturing;
            var generation = ++_producerGeneration;
            _producerTask = GenerateMockDataLoopAsync(_captureTokenSource.Token, generation);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopCaptureAsync()
    {
        Interlocked.Increment(ref _stopTransitionCount);
        Interlocked.Increment(ref _operationEpoch);
        LifecycleLease lifecycle;
        try
        {
            lifecycle = await EnterLifecycleAsync().ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _stopTransitionCount);
            throw;
        }
        Task snapCompletion;
        try
        {
            if (CurrentStatus == DeviceStatus.Disconnected)
            {
                snapCompletion = _allSnapsCompletion.Task;
                goto AwaitSnap;
            }
            _sessionCancellation.Cancel();
            CancelSnapUnderLock();
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
            lock (_disposeSync)
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
        TaskCompletionSource<bool>? snapCompletion = null;
        CancellationTokenSource? snapCancellation = null;
        LifecycleLease? snapLifecycle = null;
        try
        {
            await _snapLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            // WaitAsync may throw before acquisition. Set this only after it
            // has returned successfully so cancellation cannot over-release.
            snapLockAcquired = true;
            snapLifecycle = await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
            if (_disconnectRequested || IsStopTransitionRequested || CurrentStatus == DeviceStatus.Disconnected ||
                CurrentStatus == DeviceStatus.Snapping || snapGeneration != _operationEpoch)
                throw new InvalidOperationException("Cihaz bağlı değil.");
            CurrentStatus = DeviceStatus.Snapping;
            _sessionCancellation.Cancel();
            var producerStop = StopProducerUnderLock();
            snapLifecycle!.Release();
            snapLifecycle = null;
            await producerStop.ConfigureAwait(false);
            snapLifecycle = await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
            if (_disconnectRequested || IsStopTransitionRequested || CurrentStatus == DeviceStatus.Disconnected ||
                snapGeneration != _operationEpoch)
                throw new OperationCanceledException("Snap was stopped by a lifecycle transition.");
            _sessionCancellation.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            snapCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCancellation.Token);
            _snapCancellation = snapCancellation;
            snapCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _snapCompletion = snapCompletion;
            snapLifecycle!.Release();
            snapLifecycle = null;

            // Snap işlemi (donanımın exposure süresi kadar beklemesi simülasyonu)
            await Task.Delay((int)exposureTime, snapCancellation.Token).ConfigureAwait(false);

            var readings = new SensorReadings(
            TubeCurrent: Math.Round(50.0 + _random.NextDouble() * 5, 2), // Snap'te daha yüksek akım
            Voltage: Math.Round(70.0 + _random.NextDouble() * 30.0, 1),  // Snap'te 70-100 kV arası
            XRayIntensity: _random.Next(2000, 3000),                     // Yüksek yoğunluk
            ExposureTime: exposureTime
        );

        // Snap için tam çözünürlük (2304×2930), daha düşük gürültü
            var profile = Profile;
            ushort[] mockImage = GenerateSyntheticImage(Width, Height, noiseLevel: 500, profile, snapCancellation.Token);
        
            var frame = CreateFrame(mockImage, Width, Height, FrameKind.Snap, exposureTime, readings);
            var result = new XRayMeasurement(readings, frame, true);
            CurrentStatus = DeviceStatus.Frozen;
            return result;
        }
        finally
        {
            _isPaused = false;
            snapLifecycle?.Release();
            if (snapRegistered)
            {
                var cleanupLifecycle = await EnterLifecycleAsync(allowDispose: true).ConfigureAwait(false);
                try
                {
                    if (CurrentStatus == DeviceStatus.Snapping && !_disconnectRequested)
                        CurrentStatus = DeviceStatus.Connected;
                    if (ReferenceEquals(_snapCancellation, snapCancellation))
                        _snapCancellation = null;
                    if (ReferenceEquals(_snapCompletion, snapCompletion))
                    {
                        _snapCompletion = null;
                        snapCompletion?.TrySetResult(true);
                    }
                }
                finally { cleanupLifecycle.Release(); }
            }
            if (snapLockAcquired)
                _snapLock.Release();
            snapCancellation?.Dispose();

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
        return CurrentStatus == DeviceStatus.Connected || CurrentStatus == DeviceStatus.Frozen
            ? StartCaptureAsync() : Task.CompletedTask;
    }

    private bool _isPaused = false;

    private async Task<LifecycleLease> EnterLifecycleAsync(CancellationToken cancellationToken = default, bool allowDispose = false)
    {
        lock (_disposeSync)
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
        lock (_disposeSync)
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

    private void CancelSnapUnderLock()
    {
        // The lifecycle lock is the sole owner of the current snap CTS.  A
        // caller captures it here, before SnapAsync can clear and dispose it.
        _snapCancellation?.Cancel();
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

    private async Task GenerateMockDataLoopAsync(CancellationToken token, int generation)
    {
        _ = generation;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    await Task.Delay(100, token);
                    continue;
                }
                // Canlı yayında düşük pozlama süresi (Örn 33ms ~ 30fps)
                double liveExposure = 33.0; 

                var readings = new SensorReadings(
                    TubeCurrent: Math.Round(20.0 + _random.NextDouble() * 10, 2),
                    Voltage: Math.Round(40.0 + _random.NextDouble() * 20.0, 1), // Canlıda 40-60 kV arası
                    XRayIntensity: _random.Next(800, 1200),
                    ExposureTime: liveExposure
                );

                // Canlı yayın için düşük çözünürlüklü önizleme (512x512)
                ushort[] mockImage = GenerateSyntheticImage(LiveWidth, LiveHeight, noiseLevel: 2500, Profile);

                var frame = CreateFrame(mockImage, LiveWidth, LiveHeight, FrameKind.LivePreview, liveExposure, readings);
                var measurement = new XRayMeasurement(readings, frame, true);
                
                // Arayüze fırlat
                MeasurementReceived?.Invoke(measurement);

                // 2 FPS – UI thread'in nefes alması için
                await Task.Delay(500, token);
            }
        }
        catch (TaskCanceledException)
        {
            // Yakalama durduruldu, normal bir durum.
        }
    }

    private RawGray16Frame CreateFrame(ushort[] pixels, int width, int height, FrameKind kind, double exposure, SensorReadings readings) =>
        new(pixels, width, height, width * 2, Gray16PixelFormat.Gray16LittleEndian, kind,
            DateTimeOffset.UtcNow, Guid.NewGuid(), new AcquisitionSettings(readings.TubeCurrent, readings.Voltage, exposure, Profile), "BCL.MockCMOS",
            width * 2, "little", "top-left");

    private ushort[] GenerateSyntheticImage(int imageWidth, int imageHeight, int noiseLevel, AcquisitionProfile profile, CancellationToken cancellationToken = default)
    {
        ushort[] pixels = new ushort[imageWidth * imageHeight];

        // Ölçekleme: hangi boyutta üretilirse üretilsin aynı desen görünsün
        double scaleX = (double)imageWidth  / Width;
        double scaleY = (double)imageHeight / Height;

        Parallel.For(0, imageHeight, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            Random localRandom = new Random(y * 1000 + noiseLevel);

            // Gerçek koordinata çevir
            int realY = (int)(y / scaleY);

            for (int x = 0; x < imageWidth; x++)
            {
                int index  = y * imageWidth + x;
                int realX  = (int)(x / scaleX);
                ushort baseValue = profile == AcquisitionProfile.MedicalSpecimenLikePhantom ? (ushort)7000 : (ushort)5000;

                // 1. STEP WEDGE
                if (realY >= 200 && realY < 600 && realX >= 150 && realX < 2150)
                {
                    int step = (realX - 150) / 200;
                    if (step >= 0 && step < 10)
                        baseValue = (ushort)(10000 + step * 5500);
                }
                // 2. RESOLUTION LINES
                else if (realY >= 800 && realY < 1400)
                {
                    if      (realX >= 200  && realX < 600  && (realX / 16) % 2 == 0) baseValue = 50000;
                    else if (realX >= 700  && realX < 1000 && (realX / 8)  % 2 == 0) baseValue = 50000;
                    else if (realX >= 1100 && realX < 1300 && (realX / 4)  % 2 == 0) baseValue = 50000;
                    else if (realX >= 1400 && realX < 1600 && (realX / 2)  % 2 == 0) baseValue = 50000;
                    else if (realX >= 1700 && realX < 1900 &&  realX       % 2 == 0) baseValue = 50000;
                }
                // 3. CONTRAST CIRCLES
                else if (realY >= 1600 && realY < 2600)
                {
                    int dx1 = realX - 500,  dy1 = realY - 2100;
                    int dx2 = realX - 1150, dy2 = realY - 2100;
                    int dx3 = realX - 1800, dy3 = realY - 2100;

                    if      (dx1*dx1 + dy1*dy1 < 40000) baseValue = 45000;
                    else if (dx2*dx2 + dy2*dy2 < 22500) baseValue = 30000;
                    else if (dx3*dx3 + dy3*dy3 < 10000) baseValue = 15000;
                }

                // Gürültü
                if (profile == AcquisitionProfile.MedicalSpecimenLikePhantom && ((realX / 90 + realY / 120) % 5 == 0))
                    baseValue = (ushort)Math.Min(65535, baseValue + 9000); // synthetic material boundary
                int noise      = localRandom.Next(-noiseLevel, noiseLevel);
                int finalValue = baseValue + noise;
                pixels[index]  = (ushort)Math.Clamp(finalValue, 0, 65535);
            }
        });

        return pixels;
    }

    public void Dispose()
    {
        Task disconnectTask;
        lock (_disposeSync)
        {
            if (_disposed) return;

            _disposeRequested = true;
            if (_disconnectTask == null)
            {
                _disconnectRequested = true;
                _disconnectTask = DisconnectCoreAsync(allowDispose: true);
            }
            disconnectTask = _disconnectTask;
        }

        // DisconnectCoreAsync cancels and awaits all active Snaps before this
        // method disposes the primitives they use in their finally blocks.
        disconnectTask.GetAwaiter().GetResult();

        Task lifecycleOperations;
        lock (_disposeSync)
        {
            lifecycleOperations = _activeLifecycleOperations == 0
                ? Task.CompletedTask
                : (_lifecycleOperationsCompleted ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        lifecycleOperations.GetAwaiter().GetResult();

        lock (_disposeSync)
        {
            if (_disposed) return;
            _disposed = true;
            _captureTokenSource?.Dispose();
            _sessionCancellation.Dispose();
            _snapLock.Dispose();
            _lifecycleLock.Dispose();
        }
    }
}
