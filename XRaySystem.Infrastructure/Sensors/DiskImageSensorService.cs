using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Sensors;

/// <summary>
/// ISensorService implementasyonu: DICOM'dan türetilmiş karantina/test
/// .raw (ushort little-endian) dosyalarını diskten okuyarak X-Ray simülasyonunu sağlar.
///
/// Kullanım:
///   1. Tools/DicomConverter/dicom_to_raw.py ile DICOM → .raw dönüşümü yap.
///   2. Üretilen dosyaları ve manifest.json'u SampleData/ klasörüne koy.
///   3. DI ortamında varsayılan sensör servisi olarak çalışır.
///
/// SampleData klasörü aranma sırası:
///   1. Constructor'a verilen explicit path
///   2. %LOCALAPPDATA%\XRaySystem\SampleData\
///   3. Uygulama çalışma dizinindeki SampleData\
/// </summary>
public sealed class DiskImageSensorService : ISensorService, IDisposable
{
    // ── State ─────────────────────────────────────────────────────────────────
    private DeviceStatus _currentStatus = DeviceStatus.Disconnected;
    private CancellationTokenSource? _captureTokenSource;
    private Task? _producerTask;
    private Task? _producerStopTask;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _snapLock      = new(1, 1);
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

    // ── Görüntü kataloğu ──────────────────────────────────────────────────────
    private readonly string _sampleDataPath;
    private ManifestData? _manifest;
    private List<ImageEntry> _snapImages  = new();
    private List<ImageEntry> _liveImages  = new();
    private int _currentImageIndex = 0;
    private readonly List<string> _diagnostics = new();

    /// <summary>Resolved root and non-patient diagnostic messages for support/harnesses.</summary>
    public string ResolvedSampleDataPath => _sampleDataPath;
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public string? CurrentModality { get; private set; }
    public string? CurrentSourceFile { get; private set; }
    public string? CurrentModalityProfile { get; private set; }

    // ── Çözünürlük ────────────────────────────────────────────────────────────
    private int SnapWidth  => _manifest?.SnapWidth  ?? 2304;
    private int SnapHeight => _manifest?.SnapHeight ?? 2930;
    private int LiveWidth  => _manifest?.LiveWidth  ?? 512;
    private int LiveHeight => _manifest?.LiveHeight ?? 512;

    // ── ISensorService ────────────────────────────────────────────────────────
    public DeviceStatus CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (_currentStatus != value)
            {
                _currentStatus = value;
                PublishStatus(_currentStatus);
            }
        }
    }

    public event Action<DeviceStatus>? StatusChanged;
    public event Action<XRayMeasurement>? MeasurementReceived;
    public AcquisitionProfile Profile { get; set; } = AcquisitionProfile.IndustrialQaPhantom;
    private AcquisitionParameters _parameters = new(70.0, 3500.0, 500.0);
    public AcquisitionParameters Parameters
    {
        get => Volatile.Read(ref _parameters);
        set
        {
            value.EnsureValid();
            Volatile.Write(ref _parameters, value);
        }
    }

    // ── Ctor ──────────────────────────────────────────────────────────────────
    public DiskImageSensorService(string? sampleDataPath = null)
    {
        _sampleDataPath = ResolveSampleDataPath(sampleDataPath);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public async Task ConnectAsync()
    {
        var lifecycle = await EnterLifecycleAsync().ConfigureAwait(false);
        try
        {
            if (CurrentStatus != DeviceStatus.Disconnected) return;

            // İlk bağlantıda manifest yükle
            await Task.Run(LoadManifest);

            lock (_disposeLock)
            {
                if (_disposeRequested || _disposed)
                    throw new ObjectDisposedException(nameof(DiskImageSensorService));
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
            else if (_disposed)          { return; }
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
                if (_disposeRequested || _disposed) throw new ObjectDisposedException(nameof(DiskImageSensorService));
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

            // Gerçek DICOM'dan piksel yükle
            var pixels = LoadSnapPixels(snapCts.Token);

            var parameters = Parameters;
            var readings = new SensorReadings(
                TubeCurrent:  Math.Round(parameters.TubeCurrentMa + (_random.NextDouble() - 0.5) * 0.2, 2),
                Voltage:      Math.Round(parameters.VoltageKv + (_random.NextDouble() - 0.5) * 0.4, 1),
                XRayIntensity: _random.Next(2000, 3000),
                ExposureTime: exposureTime
            );

            var frame = new RawGray16Frame(
                pixels, SnapWidth, SnapHeight, SnapWidth * 2,
                Gray16PixelFormat.Gray16LittleEndian,
                FrameKind.Snap,
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                new AcquisitionSettings(readings.TubeCurrent, readings.Voltage, exposureTime, Profile),
                "DiskImageSensor:QuarantinedSample",
                SnapWidth * 2, "little", "top-left");

            CurrentStatus = DeviceStatus.Frozen;
            // Dosyadan türetilen/quarantine görüntü hiçbir zaman gerçek cihaz acquisition'ı sayılmaz.
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
                var parameters = Parameters;
                var readings = new SensorReadings(
                    TubeCurrent:   Math.Round(parameters.TubeCurrentMa + (_random.NextDouble() - 0.5) * 0.3, 2),
                    Voltage:       Math.Round(parameters.VoltageKv + (_random.NextDouble() - 0.5) * 0.6, 1),
                    XRayIntensity: _random.Next(800, 1200),
                    ExposureTime:  liveExposure
                );

                ushort[] livePixels;
                try
                {
                    livePixels = LoadLivePixels(token);
                }
                catch (OperationCanceledException) { break; }

                var frame = new RawGray16Frame(
                    livePixels, LiveWidth, LiveHeight, LiveWidth * 2,
                    Gray16PixelFormat.Gray16LittleEndian,
                    FrameKind.LivePreview,
                    DateTimeOffset.UtcNow,
                    Guid.NewGuid(),
                    new AcquisitionSettings(readings.TubeCurrent, readings.Voltage, liveExposure, Profile),
                    "DiskImageSensor:QuarantinedSample",
                    LiveWidth * 2, "little", "top-left");

                var measurement = new XRayMeasurement(readings, frame, isSimulator: true);
                if (token.IsCancellationRequested || generation != Volatile.Read(ref _producerGeneration))
                    break;
                if (!PublishMeasurement(measurement))
                {
                    CurrentStatus = DeviceStatus.Error;
                    break;
                }

                // 2 FPS
                await Task.Delay(500, token);
            }
        }
        catch (TaskCanceledException) { }
    }

    // ── Piksel yükleme ────────────────────────────────────────────────────────

    /// <summary>
    /// Snap için tam çözünürlüklü piksel verisi yükler.
    /// Her Snap çağrısında sırayla farklı görüntü kullanır (round-robin).
    /// </summary>
    private ushort[] LoadSnapPixels(CancellationToken cancellationToken)
    {
        if (_snapImages.Count == 0)
            throw new InvalidOperationException(
                "SampleData klasöründe hiç snap görüntüsü bulunamadı. " +
                "Tools/DicomConverter/dicom_to_raw.py ile dönüşüm yapın.");

        var entry = _snapImages[_currentImageIndex % _snapImages.Count];
        _currentImageIndex++;

        RecordSelection(entry, "snap");
        return LoadRawFile(entry.FullPath, SnapWidth * SnapHeight, cancellationToken);
    }

    /// <summary>
    /// Live preview için küçük çözünürlüklü piksel verisi yükler.
    /// </summary>
    private ushort[] LoadLivePixels(CancellationToken cancellationToken)
    {
        if (_liveImages.Count == 0)
            throw new InvalidOperationException("Canlı görüntü bulunamadı.");

        var entry = _liveImages[_currentImageIndex % _liveImages.Count];
        RecordSelection(entry, "live");
        return LoadRawFile(entry.FullPath, LiveWidth * LiveHeight, cancellationToken);
    }

    private static ushort[] LoadRawFile(string path, int expectedPixelCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = File.ReadAllBytes(path);
        int byteCount = expectedPixelCount * 2; // ushort = 2 byte

        if (bytes.Length < byteCount)
            throw new InvalidDataException(
                $"Dosya boyutu beklenen değerden küçük: {path}\n" +
                $"  Beklenen: {byteCount} byte ({expectedPixelCount} piksel)\n" +
                $"  Gerçek:   {bytes.Length} byte");

        // byte[] → ushort[] (little-endian, zero-copy on LE systems)
        var pixels = new ushort[expectedPixelCount];
        Buffer.BlockCopy(bytes, 0, pixels, 0, byteCount);

        // Büyük-endian sistem varsa (nadiren) byte swap yap
        if (!BitConverter.IsLittleEndian)
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (ushort)((pixels[i] >> 8) | (pixels[i] << 8));

        return pixels;
    }

    // ── Manifest & dosya keşfi ────────────────────────────────────────────────
    private void LoadManifest()
    {
        _snapImages.Clear();
        _liveImages.Clear();
        _diagnostics.Clear();
        Diagnostic($"SampleData root: {_sampleDataPath}");
        var manifestPath = Path.Combine(_sampleDataPath, "manifest.json");

        if (File.Exists(manifestPath))
        {
            // manifest.json varsa onu kullan
            // PropertyNamingPolicy.SnakeCaseLower: "snap_file" → SnapFile eşlemesini sağlar
            var json = File.ReadAllText(manifestPath);
            _manifest = JsonSerializer.Deserialize<ManifestData>(json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });

            if (_manifest?.Images is { } images)
            {
                if (images.Any(image => string.Equals(image.Modality, "CT", StringComparison.OrdinalIgnoreCase)))
                    Profile = AcquisitionProfile.MedicalSpecimenLikePhantom;
                foreach (var img in images)
                {
                    if (string.IsNullOrEmpty(img.SnapFile) || string.IsNullOrEmpty(img.LiveFile))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DiskImageSensor] Manifest girişi atlandı — SnapFile veya LiveFile null: {img.Stem}");
                        continue;
                    }

                    var snapPath = Path.Combine(_sampleDataPath, img.SnapFile);
                    var livePath = Path.Combine(_sampleDataPath, img.LiveFile);

                    if (File.Exists(snapPath))
                        _snapImages.Add(new ImageEntry(snapPath, img.Stem ?? img.EntryId ?? "(unnamed)", img.Modality ?? "", img.SourceFile ?? ""));
                    else
                        System.Diagnostics.Debug.WriteLine($"[DiskImageSensor] Snap dosyası bulunamadı: {snapPath}");

                    if (File.Exists(livePath))
                        _liveImages.Add(new ImageEntry(livePath, img.Stem ?? img.EntryId ?? "(unnamed)", img.Modality ?? "", img.SourceFile ?? ""));
                    else
                        System.Diagnostics.Debug.WriteLine($"[DiskImageSensor] Live dosyası bulunamadı: {livePath}");
                }
                CheckDuplicateManifestAssets(images);
            }
        }
        else
        {
            // manifest.json yoksa klasördeki tüm .raw dosyalarını tara
            var rawFiles = Directory.GetFiles(_sampleDataPath, "*.raw");
            foreach (var f in rawFiles)
            {
                var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (name.Contains("_snap_"))
                    _snapImages.Add(new ImageEntry(f, Path.GetFileNameWithoutExtension(f), "", ""));
                else if (name.Contains("_live_"))
                    _liveImages.Add(new ImageEntry(f, Path.GetFileNameWithoutExtension(f), "", ""));
                else
                    _snapImages.Add(new ImageEntry(f, Path.GetFileNameWithoutExtension(f), "", "")); // bilinmeyen → snap olarak dene
            }
        }

        if (_snapImages.Count == 0)
            System.Diagnostics.Debug.WriteLine(
                $"[DiskImageSensor] UYARI: {_sampleDataPath} içinde snap görüntüsü bulunamadı. " +
                "dicom_to_raw.py çalıştırılmış mı?");
        else
            System.Diagnostics.Debug.WriteLine(
                $"[DiskImageSensor] {_snapImages.Count} snap + {_liveImages.Count} live görüntü yüklendi.");

    }

    private void RecordSelection(ImageEntry entry, string kind)
    {
        CurrentModality = entry.Modality;
        CurrentSourceFile = entry.SourceFile;
        CurrentModalityProfile = MapModalityProfile(entry.Modality);
        Diagnostic($"Selected {kind}: entry={entry.EntryId}; source={entry.SourceFile}; file={Path.GetFileName(entry.FullPath)}");
    }

    private void CheckDuplicateManifestAssets(IEnumerable<ManifestImage> images)
    {
        CheckDuplicateRole(images, image => image.SnapFile, "snap");
        CheckDuplicateRole(images, image => image.LiveFile, "live");
    }

    private void CheckDuplicateRole(IEnumerable<ManifestImage> images, Func<ManifestImage, string?> fileSelector, string role)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            var filename = fileSelector(image);
            if (string.IsNullOrWhiteSpace(filename)) continue;
            var path = Path.Combine(_sampleDataPath, filename);
            if (!File.Exists(path)) continue;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            var entryId = image.Stem ?? image.EntryId ?? "(unnamed)";
            if (hashes.TryGetValue(hash, out var previous) && !string.Equals(previous, entryId, StringComparison.OrdinalIgnoreCase))
                Diagnostic($"INTEGRITY WARNING: manifest {role} assets for entries {previous} and {entryId} are byte-identical.");
            else
                hashes[hash] = entryId;
        }
    }

    private static string MapModalityProfile(string? modality) =>
        string.Equals(modality, "CT", StringComparison.OrdinalIgnoreCase)
            ? nameof(AcquisitionProfile.MedicalSpecimenLikePhantom)
            : "unmapped-domain-profile"; // CR/DX have no distinct domain enum; retain Profile.

    private void PublishStatus(DeviceStatus status)
    {
        if (StatusChanged is not { } handlers) return;
        foreach (Action<DeviceStatus> handler in handlers.GetInvocationList())
        {
            try { handler(status); }
            catch (Exception ex) { Diagnostic($"Status handler error: {ex.Message}"); }
        }
    }

    private bool PublishMeasurement(XRayMeasurement measurement)
    {
        if (MeasurementReceived is not { } handlers) return true;
        var succeeded = true;
        foreach (Action<XRayMeasurement> handler in handlers.GetInvocationList())
        {
            try { handler(measurement); }
            catch (Exception ex)
            {
                succeeded = false;
                Diagnostic($"Measurement handler error: {ex.Message}");
            }
        }
        return succeeded;
    }

    private void Diagnostic(string message)
    {
        _diagnostics.Add(message);
        System.Diagnostics.Debug.WriteLine($"[DiskImageSensor] {message}");
    }

    private static string ResolveSampleDataPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
            return explicitPath;

        // %LOCALAPPDATA%\XRaySystem\SampleData
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XRaySystem", "SampleData");
        if (Directory.Exists(appData)) return appData;

        // Çalışma dizinindeki SampleData
        var local = Path.Combine(AppContext.BaseDirectory, "SampleData");
        if (Directory.Exists(local)) return local;

        // Proje içi SampleData (geliştirme zamanı)
        var devPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "XRaySystem.Infrastructure", "SampleData");
        var absDevPath = Path.GetFullPath(devPath);
        if (Directory.Exists(absDevPath)) return absDevPath;

        // Hiçbiri yoksa oluştur ve uyar
        Directory.CreateDirectory(appData);
        return appData;
    }

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
                throw new ObjectDisposedException(nameof(DiskImageSensorService));
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
        private readonly DiskImageSensorService _owner;
        private int _released;

        public LifecycleLease(DiskImageSensorService owner) => _owner = owner;

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

    // ── İç veri yapıları ──────────────────────────────────────────────────────
    private sealed record ImageEntry(string FullPath, string EntryId, string Modality, string SourceFile);

    private sealed class ManifestData
    {
        public int Version    { get; set; }
        public int SnapWidth  { get; set; } = 2304;
        public int SnapHeight { get; set; } = 2930;
        public int LiveWidth  { get; set; } = 512;
        public int LiveHeight { get; set; } = 512;
        public List<ManifestImage>? Images { get; set; }
        public string? RawRepresentation { get; set; }
        public string? NormalizationPolicy { get; set; }
        public int NormalizationPolicyVersion { get; set; }
        public bool PhysicalCalibrationInRaw { get; set; }
    }

    private sealed class ManifestImage
    {
        public string? Stem        { get; set; }
        public string? EntryId     { get; set; }
        public string? SnapFile    { get; set; }
        public string? LiveFile    { get; set; }
        public string? Modality    { get; set; }
        public string? Kvp         { get; set; }
        public string? ExposureTime { get; set; }
        public string? TubeCurrent  { get; set; }
        public string? SourceFile   { get; set; }
        public double? RescaleSlope { get; set; }
        public double? RescaleIntercept { get; set; }
        public string? NormalizationPolicy { get; set; }
        public int NormalizationPolicyVersion { get; set; }
    }
}
