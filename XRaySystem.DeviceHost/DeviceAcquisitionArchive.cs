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

public interface IDeviceAcquisitionArchive
{
    DeviceAcquisitionArchiveReceipt Save(
        XRayMeasurement measurement,
        RemoteImageMetadataContract metadata,
        byte[] lowPreviewBmp);

    IReadOnlyList<ArchivedRemoteImage> LoadRecent(int maximumCount);
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

        var recoveredNewestFirst = new List<ArchivedRemoteImage>(maximumCount);
        foreach (var manifestPath in Directory
                     .EnumerateFiles(_options.BaseFolder, "*" + ManifestExtension, SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                recoveredNewestFirst.Add(LoadOne(manifestPath));
                if (recoveredNewestFirst.Count == maximumCount) break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException or ArgumentException)
            {
                _logger.LogWarning(
                    ex,
                    "Archived acquisition ignored after verification failure: Manifest={Manifest}",
                    Path.GetFileName(manifestPath));
                if (_options.QuarantineIncompleteArtifacts)
                    QuarantineArtifactSet(manifestPath);
            }
        }

        recoveredNewestFirst.Reverse();
        return recoveredNewestFirst;
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
        var rawPath = Path.Combine(_options.BaseFolder, stem + ".xrayraw");
        var previewPath = Path.Combine(_options.BaseFolder, stem + ".bmp");
        var frame = _storage.LoadRaw(rawPath);
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
            throw new InvalidDataException("Acquisition RAW ve manifest metadata uyuşmuyor.");

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
            var complete = paths.Any(path => path.EndsWith(".xrayraw", StringComparison.OrdinalIgnoreCase)) &&
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
