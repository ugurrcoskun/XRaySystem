using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;
using XRaySystem.Contracts;
using System.Security.Cryptography;
using System.Text.Json;

namespace XRaySystem.DeviceHost;

public sealed record DeviceAcquisitionArchiveOptions(
    bool Enabled,
    string BaseFolder,
    bool RequireSignedMetadata = false,
    string? IntegrityKey = null,
    bool QuarantineIncompleteArtifacts = true,
    double IncompleteArtifactGracePeriodMinutes = 10)
{
    public void EnsureValid()
    {
        if (!Enabled) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(BaseFolder);
        var fullPath = Path.GetFullPath(BaseFolder);
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Acquisition storage bir disk kökü olamaz.", nameof(BaseFolder));
        if (RequireSignedMetadata && string.IsNullOrWhiteSpace(IntegrityKey))
            throw new ArgumentException("İmzalı acquisition metadata için integrity key zorunludur.", nameof(IntegrityKey));
        if (!string.IsNullOrWhiteSpace(IntegrityKey) && IntegrityKey.Trim().Length < 32)
            throw new ArgumentException("Acquisition integrity key en az 32 karakter olmalıdır.", nameof(IntegrityKey));
        if (!double.IsFinite(IncompleteArtifactGracePeriodMinutes) ||
            IncompleteArtifactGracePeriodMinutes is < 0 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(IncompleteArtifactGracePeriodMinutes));
    }
}

public sealed record DeviceAcquisitionArchiveReceipt(
    bool Persisted,
    string? FileStem,
    DateTimeOffset? PersistedAtUtc)
{
    public static DeviceAcquisitionArchiveReceipt Disabled { get; } = new(false, null, null);
}

public sealed record ArchivedRemoteImage(
    RemoteImageMetadataContract Metadata,
    RawGray16Frame Frame,
    byte[] LowPreviewBmp);

public sealed class AcquisitionPersistenceException : IOException
{
    public AcquisitionPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IDeviceAcquisitionCatalog
{
    IReadOnlyList<RemoteImageMetadataContract> GetMetadataPage(int skip, int take) => [];

    IReadOnlyList<RemoteImageMetadataContract> GetMetadataPage(
        DateTimeOffset? beforeCapturedAtUtc,
        Guid? beforeImageId,
        int take) => [];

    bool TryGetMetadata(Guid imageId, out RemoteImageMetadataContract? metadata)
    {
        metadata = null;
        return false;
    }
}

public interface IDeviceAcquisitionArchive : IDeviceAcquisitionCatalog
{
    DeviceAcquisitionArchiveReceipt Save(
        XRayMeasurement measurement,
        RemoteImageMetadataContract metadata,
        byte[] lowPreviewBmp);

    IReadOnlyList<ArchivedRemoteImage> LoadRecent(int maximumCount);

    bool TryLoad(Guid imageId, out ArchivedRemoteImage? image)
    {
        image = null;
        return false;
    }
}

public sealed class DeviceAcquisitionArchive : IDeviceAcquisitionArchive
{
    private const int ManifestSchemaVersion = 2;
    private const string ManifestExtension = ".xraymeta.json";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeviceAcquisitionArchiveOptions _options;
    private readonly AcquisitionArtifactStorage? _storage;
    private readonly ILogger<DeviceAcquisitionArchive> _logger;
    private readonly byte[]? _integrityKey;
    private readonly object _catalogGate = new();
    private readonly Dictionary<Guid, CatalogEntry> _catalog = [];
    private bool _catalogLoaded;

    private sealed record CatalogEntry(
        RemoteImageMetadataContract Metadata,
        string ManifestPath);

    public DeviceAcquisitionArchive(
        DeviceAcquisitionArchiveOptions options,
        ILogger<DeviceAcquisitionArchive> logger)
    {
        options.EnsureValid();
        _options = options;
        _logger = logger;
        _integrityKey = string.IsNullOrWhiteSpace(options.IntegrityKey)
            ? null
            : SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(options.IntegrityKey.Trim()));
        _storage = options.Enabled
            ? new AcquisitionArtifactStorage(options.BaseFolder)
            : null;
        if (options.Enabled)
        {
            if (options.QuarantineIncompleteArtifacts)
                QuarantineStaleIncompleteArtifacts();
            VerifyWritable(options.BaseFolder);
        }
    }

    public DeviceAcquisitionArchiveReceipt Save(
        XRayMeasurement measurement,
        RemoteImageMetadataContract metadata,
        byte[] lowPreviewBmp)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(lowPreviewBmp);
        if (!_options.Enabled || _storage is null)
            return DeviceAcquisitionArchiveReceipt.Disabled;

        var frame = measurement.Frame
            ?? throw new AcquisitionPersistenceException(
                "Acquisition kalıcı kayda alınamadı: frame bulunamadı.",
                new InvalidDataException("Ölçüm frame içermiyor."));
        try
        {
            if (metadata.ImageId != measurement.Id || metadata.AcquisitionId != frame.AcquisitionId)
                throw new InvalidDataException("Kalıcı metadata ölçüm/acquisition kimliğiyle uyuşmuyor.");
            var persistedAt = DateTimeOffset.UtcNow;
            var persistedMetadata = metadata with { PersistedOnDevice = true };
            var payload = new PersistedAcquisitionManifestPayload(
                ManifestSchemaVersion,
                persistedAt,
                Convert.ToHexString(SHA256.HashData(lowPreviewBmp)),
                persistedMetadata);
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, ManifestJsonOptions);
            var signature = _integrityKey is null
                ? null
                : Convert.ToHexString(HMACSHA256.HashData(_integrityKey, payloadBytes));
            var manifest = new PersistedAcquisitionManifest(
                payload.SchemaVersion,
                payload.PersistedAtUtc,
                payload.PreviewSha256,
                payload.Metadata,
                signature);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
            var stem = AcquisitionFileNameFactory.CreateStem(measurement);
            var files = _storage.SaveWithMetadata(
                frame,
                lowPreviewBmp,
                ".bmp",
                stem,
                manifestBytes,
                ManifestExtension);
            lock (_catalogGate)
            {
                if (_catalogLoaded)
                {
                    _catalog[persistedMetadata.ImageId] = new CatalogEntry(
                        persistedMetadata,
                        Path.Combine(_options.BaseFolder, files.FileStem + ManifestExtension));
                }
            }
            _logger.LogInformation(
                "Acquisition persisted: AcquisitionId={AcquisitionId} ImageId={ImageId} FileStem={FileStem}",
                frame.AcquisitionId,
                measurement.Id,
                files.FileStem);
            return new DeviceAcquisitionArchiveReceipt(true, files.FileStem, persistedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            throw new AcquisitionPersistenceException(
                $"Acquisition {frame.AcquisitionId:D} cihaz bilgisayarında kalıcı kayda alınamadı.",
                ex);
        }
    }

    public IReadOnlyList<ArchivedRemoteImage> LoadRecent(int maximumCount)
    {
        if (maximumCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        if (!_options.Enabled || _storage is null)
            return [];

        EnsureCatalogLoaded();
        CatalogEntry[] entries;
        lock (_catalogGate)
        {
            entries = _catalog.Values
                .OrderByDescending(entry => entry.Metadata.CapturedAtUtc)
                .ThenByDescending(entry => entry.Metadata.ImageId)
                .Take(maximumCount)
                .ToArray();
        }

        var recoveredNewestFirst = new List<ArchivedRemoteImage>(entries.Length);
        foreach (var entry in entries)
        {
            try
            {
                recoveredNewestFirst.Add(LoadOne(entry.ManifestPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException or ArgumentException)
            {
                _logger.LogWarning(
                    ex,
                    "Archived acquisition ignored after verification failure: Manifest={Manifest}",
                    Path.GetFileName(entry.ManifestPath));
                RemoveCatalogEntry(entry.Metadata.ImageId);
                if (_options.QuarantineIncompleteArtifacts)
                    QuarantineArtifactSet(entry.ManifestPath);
            }
        }

        recoveredNewestFirst.Reverse();
        return recoveredNewestFirst;
    }

    public IReadOnlyList<RemoteImageMetadataContract> GetMetadataPage(int skip, int take)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(take));
        if (!_options.Enabled || _storage is null) return [];

        EnsureCatalogLoaded();
        lock (_catalogGate)
        {
            return _catalog.Values
                .Select(entry => entry.Metadata)
                .OrderByDescending(metadata => metadata.CapturedAtUtc)
                .ThenByDescending(metadata => metadata.ImageId)
                .Skip(skip)
                .Take(take)
                .ToArray();
        }
    }

    public IReadOnlyList<RemoteImageMetadataContract> GetMetadataPage(
        DateTimeOffset? beforeCapturedAtUtc,
        Guid? beforeImageId,
        int take)
    {
        if (take is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(take));
        if (beforeCapturedAtUtc.HasValue != beforeImageId.HasValue)
            throw new ArgumentException("Cursor timestamp ve image ID birlikte verilmelidir.");
        if (!_options.Enabled || _storage is null) return [];

        EnsureCatalogLoaded();
        lock (_catalogGate)
        {
            var query = _catalog.Values.Select(entry => entry.Metadata);
            if (beforeCapturedAtUtc is { } capturedAt && beforeImageId is { } imageId)
            {
                query = query.Where(metadata =>
                    metadata.CapturedAtUtc < capturedAt ||
                    (metadata.CapturedAtUtc == capturedAt && metadata.ImageId.CompareTo(imageId) < 0));
            }

            return query
                .OrderByDescending(metadata => metadata.CapturedAtUtc)
                .ThenByDescending(metadata => metadata.ImageId)
                .Take(take)
                .ToArray();
        }
    }

    public bool TryLoad(Guid imageId, out ArchivedRemoteImage? image)
    {
        if (imageId == Guid.Empty) throw new ArgumentException("ImageId boş olamaz.", nameof(imageId));
        image = null;
        if (!_options.Enabled || _storage is null) return false;

        EnsureCatalogLoaded();
        CatalogEntry? entry;
        lock (_catalogGate) _catalog.TryGetValue(imageId, out entry);
        if (entry is null) return false;

        try
        {
            image = LoadOne(entry.ManifestPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException or ArgumentException)
        {
            _logger.LogWarning(
                ex,
                "Archived acquisition ignored after verification failure: Manifest={Manifest}",
                Path.GetFileName(entry.ManifestPath));
            RemoveCatalogEntry(imageId);
            if (_options.QuarantineIncompleteArtifacts)
                QuarantineArtifactSet(entry.ManifestPath);
            return false;
        }
    }

    public bool TryGetMetadata(Guid imageId, out RemoteImageMetadataContract? metadata)
    {
        if (imageId == Guid.Empty) throw new ArgumentException("ImageId boş olamaz.", nameof(imageId));
        metadata = null;
        if (!_options.Enabled || _storage is null) return false;
        EnsureCatalogLoaded();
        lock (_catalogGate)
        {
            if (!_catalog.TryGetValue(imageId, out var entry)) return false;
            metadata = entry.Metadata;
            return true;
        }
    }

    private void EnsureCatalogLoaded()
    {
        lock (_catalogGate)
        {
            if (_catalogLoaded) return;
            _catalog.Clear();
            foreach (var manifestPath in Directory
                         .EnumerateFiles(_options.BaseFolder, "*" + ManifestExtension, SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var archived = LoadOne(manifestPath);
                    _catalog[archived.Metadata.ImageId] = new CatalogEntry(
                        archived.Metadata,
                        manifestPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException or ArgumentException)
                {
                    _logger.LogWarning(
                        ex,
                        "Archived acquisition ignored while catalog was rebuilt: Manifest={Manifest}",
                        Path.GetFileName(manifestPath));
                    if (_options.QuarantineIncompleteArtifacts)
                        QuarantineArtifactSet(manifestPath);
                }
            }
            _catalogLoaded = true;
        }
    }

    private void RemoveCatalogEntry(Guid imageId)
    {
        lock (_catalogGate) _catalog.Remove(imageId);
    }

    private ArchivedRemoteImage LoadOne(string manifestPath)
    {
        var manifestBytes = _storage!.LoadMetadata(manifestPath);
        var manifest = JsonSerializer.Deserialize<PersistedAcquisitionManifest>(
            manifestBytes,
            ManifestJsonOptions)
            ?? throw new InvalidDataException("Acquisition manifest boş.");
        if (manifest.SchemaVersion is not (1 or ManifestSchemaVersion))
            throw new InvalidDataException("Acquisition manifest sürümü desteklenmiyor.");
        if (!manifest.Metadata.PersistedOnDevice ||
            manifest.Metadata.ImageId == Guid.Empty ||
            manifest.Metadata.AcquisitionId == Guid.Empty)
            throw new InvalidDataException("Acquisition manifest kimliği veya persistence durumu geçersiz.");
        VerifyManifestSignature(manifest);

        var fileName = Path.GetFileName(manifestPath);
        if (!fileName.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Acquisition manifest uzantısı geçersiz.");
        var stem = fileName[..^ManifestExtension.Length];
        var sourcePath = ResolveSourcePath(stem);
        var previewPath = Path.Combine(_options.BaseFolder, stem + ".bmp");
        var frame = _storage.LoadRaw(sourcePath);
        var preview = _storage.LoadPreview(previewPath);

        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(preview)),
                manifest.PreviewSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Acquisition preview hash doğrulaması başarısız.");
        if (frame.AcquisitionId != manifest.Metadata.AcquisitionId ||
            frame.Width != manifest.Metadata.Width ||
            frame.Height != manifest.Metadata.Height ||
            frame.StrideBytes != manifest.Metadata.StrideBytes ||
            frame.Operation != AcquisitionOperation.Snap ||
            manifest.Metadata.Operation != AcquisitionOperationContract.Snap)
            throw new InvalidDataException("Acquisition Gray16 TIFF ve manifest metadata uyuşmuyor.");

        return new ArchivedRemoteImage(manifest.Metadata, frame, preview);
    }

    private void VerifyManifestSignature(PersistedAcquisitionManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.IntegrityHmacSha256))
        {
            if (_options.RequireSignedMetadata)
                throw new InvalidDataException("Acquisition manifest imzası zorunlu fakat bulunamadı.");
            return;
        }
        if (_integrityKey is null)
            throw new InvalidDataException("İmzalı acquisition manifest doğrulama anahtarı bulunamadı.");

        var payload = new PersistedAcquisitionManifestPayload(
            manifest.SchemaVersion,
            manifest.PersistedAtUtc,
            manifest.PreviewSha256,
            manifest.Metadata);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, ManifestJsonOptions);
        var expected = HMACSHA256.HashData(_integrityKey, payloadBytes);
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(manifest.IntegrityHmacSha256);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Acquisition manifest imza biçimi geçersiz.", ex);
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("Acquisition manifest HMAC doğrulaması başarısız.");
    }

    private void QuarantineStaleIncompleteArtifacts()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_options.IncompleteArtifactGracePeriodMinutes);
        var files = Directory
            .EnumerateFiles(_options.BaseFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsArtifactOrTemporary(Path.GetFileName(path)))
            .ToArray();

        foreach (var temporary in files.Where(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
        {
            if (File.GetLastWriteTimeUtc(temporary) <= cutoff)
                QuarantineFiles([temporary]);
        }

        var artifactGroups = files
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .GroupBy(GetArtifactStem, StringComparer.OrdinalIgnoreCase);
        foreach (var group in artifactGroups)
        {
            var paths = group.ToArray();
            var complete = paths.Any(path =>
                               path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".xrayraw", StringComparison.OrdinalIgnoreCase)) &&
                           paths.Any(path => path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) &&
                           paths.Any(path => path.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase));
            if (complete || paths.Any(path => File.GetLastWriteTimeUtc(path) > cutoff)) continue;
            QuarantineFiles(paths);
        }
    }

    private void QuarantineArtifactSet(string manifestPath)
    {
        var fileName = Path.GetFileName(manifestPath);
        if (!fileName.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase)) return;
        var stem = fileName[..^ManifestExtension.Length];
        QuarantineFiles(
            [
                manifestPath,
                Path.Combine(_options.BaseFolder, stem + ".tiff"),
                Path.Combine(_options.BaseFolder, stem + ".tif"),
                Path.Combine(_options.BaseFolder, stem + ".xrayraw"),
                Path.Combine(_options.BaseFolder, stem + ".bmp")
            ]);
    }

    private void QuarantineFiles(IEnumerable<string> paths)
    {
        var existing = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length == 0) return;
        var quarantineFolder = Path.Combine(
            _options.BaseFolder,
            "Quarantine",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(quarantineFolder);
        foreach (var path in existing)
        {
            try
            {
                var destination = Path.Combine(quarantineFolder, Path.GetFileName(path));
                if (File.Exists(destination))
                    destination = Path.Combine(
                        quarantineFolder,
                        Path.GetFileNameWithoutExtension(path) + "_" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                File.Move(path, destination);
                _logger.LogWarning(
                    "Acquisition artifact moved to quarantine: Artifact={Artifact}",
                    Path.GetFileName(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    ex,
                    "Acquisition artifact could not be quarantined: Artifact={Artifact}",
                    Path.GetFileName(path));
            }
        }
    }

    private static bool IsArtifactOrTemporary(string fileName) =>
        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".xrayraw", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase);

    private static string GetArtifactStem(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase))
            return fileName[..^ManifestExtension.Length];
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private string ResolveSourcePath(string stem)
    {
        var tiffPath = Path.Combine(_options.BaseFolder, stem + ".tiff");
        if (File.Exists(tiffPath)) return tiffPath;
        var tifPath = Path.Combine(_options.BaseFolder, stem + ".tif");
        if (File.Exists(tifPath)) return tifPath;
        // Read-only migration path for archives written before TIFF became the
        // canonical persisted source. New writes never create this extension.
        return Path.Combine(_options.BaseFolder, stem + ".xrayraw");
    }

    private static void VerifyWritable(string baseFolder)
    {
        var probePath = Path.Combine(baseFolder, $".devicehost-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.WriteByte(0x58);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probePath)) File.Delete(probePath);
        }
    }

    private sealed record PersistedAcquisitionManifest(
        int SchemaVersion,
        DateTimeOffset PersistedAtUtc,
        string PreviewSha256,
        RemoteImageMetadataContract Metadata,
        string? IntegrityHmacSha256);

    private sealed record PersistedAcquisitionManifestPayload(
        int SchemaVersion,
        DateTimeOffset PersistedAtUtc,
        string PreviewSha256,
        RemoteImageMetadataContract Metadata);
}

internal sealed class NullDeviceAcquisitionArchive : IDeviceAcquisitionArchive
{
    public static NullDeviceAcquisitionArchive Instance { get; } = new();

    public DeviceAcquisitionArchiveReceipt Save(
        XRayMeasurement measurement,
        RemoteImageMetadataContract metadata,
        byte[] lowPreviewBmp) =>
        DeviceAcquisitionArchiveReceipt.Disabled;

    public IReadOnlyList<ArchivedRemoteImage> LoadRecent(int maximumCount) => [];
}
