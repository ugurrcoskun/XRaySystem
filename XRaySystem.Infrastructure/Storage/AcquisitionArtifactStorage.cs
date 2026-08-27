using XRaySystem.Domain.ValueObjects;
using System.Text.Json;

namespace XRaySystem.Infrastructure.Storage;

internal enum AcquisitionArtifactWriteStage
{
    PreviewTempFlushed,
    MetadataTempFlushed,
    RawTempVerified,
    RawPromoted,
    PreviewPromoted,
    MetadataPromoted
}

public sealed record AcquisitionArtifactFiles(
    string FileStem,
    string PreviewPath,
    string RawPath,
    string? MetadataPath = null);

public sealed record AcquisitionPreviewFile(string FileStem, string PreviewPath);

/// <summary>
/// Writes the canonical Gray16 TIFF source and its display derivative as one
/// recoverable pair. Temporary files are verified before either final name is
/// made visible.
/// </summary>
public sealed class AcquisitionArtifactStorage
{
    private const string SourceTiffExtension = ".tiff";
    private const string LegacyRawExtension = ".xrayraw";
    private const int MaxCollisionAttempts = 100;
    private readonly string _baseFolder;
    private readonly object _writeGate = new();
    private readonly Action<AcquisitionArtifactWriteStage>? _faultInjector;

    public AcquisitionArtifactStorage(string baseFolder) : this(baseFolder, null)
    {
    }

    internal AcquisitionArtifactStorage(
        string baseFolder,
        Action<AcquisitionArtifactWriteStage>? faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseFolder);
        _baseFolder = Path.GetFullPath(baseFolder);
        _faultInjector = faultInjector;
        Directory.CreateDirectory(_baseFolder);
    }

    public AcquisitionArtifactFiles Save(
        RawGray16Frame rawFrame,
        byte[] previewBytes,
        string previewExtension,
        string requestedFileStem) =>
        SaveCore(rawFrame, previewBytes, previewExtension, requestedFileStem, null, null);

    public AcquisitionArtifactFiles SaveWithMetadata(
        RawGray16Frame rawFrame,
        byte[] previewBytes,
        string previewExtension,
        string requestedFileStem,
        byte[] metadataBytes,
        string metadataExtension = ".xraymeta.json")
    {
        ArgumentNullException.ThrowIfNull(metadataBytes);
        if (metadataBytes.Length == 0)
            throw new ArgumentException("Acquisition metadata boş olamaz.", nameof(metadataBytes));
        using var _ = JsonDocument.Parse(metadataBytes);
        var normalizedMetadataExtension = metadataExtension.StartsWith('.')
            ? metadataExtension.ToLowerInvariant()
            : "." + metadataExtension.ToLowerInvariant();
        if (normalizedMetadataExtension != ".xraymeta.json")
            throw new NotSupportedException("Yalnız .xraymeta.json acquisition metadata saklanabilir.");
        return SaveCore(
            rawFrame,
            previewBytes,
            previewExtension,
            requestedFileStem,
            metadataBytes,
            normalizedMetadataExtension);
    }

    public AcquisitionPreviewFile SavePreviewOnly(
        byte[] previewBytes,
        string previewExtension,
        string requestedFileStem,
        Guid acquisitionId)
    {
        ArgumentNullException.ThrowIfNull(previewBytes);
        if (acquisitionId == Guid.Empty)
            throw new ArgumentException("Acquisition ID boş olamaz.", nameof(acquisitionId));
        var extension = NormalizeAndValidatePreview(previewBytes, previewExtension);
        var baseStem = SanitizeFileStem(requestedFileStem);

        lock (_writeGate)
        {
            for (var attempt = 0; attempt < MaxCollisionAttempts; attempt++)
            {
                var stem = CreateCandidateStem(baseStem, acquisitionId, attempt);
                var previewPath = GetPreviewPath(stem, extension, sourceIncluded: false);
                var sourcePath = Path.Combine(_baseFolder, stem + SourceTiffExtension);
                var legacyRawPath = Path.Combine(_baseFolder, stem + LegacyRawExtension);
                if (File.Exists(previewPath) || File.Exists(sourcePath) || File.Exists(legacyRawPath)) continue;

                var temporaryPath = previewPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    WriteBytes(temporaryPath, previewBytes);
                    if (!File.ReadAllBytes(temporaryPath).AsSpan().SequenceEqual(previewBytes))
                        throw new InvalidDataException("Yazılan preview bütünlük doğrulaması başarısız.");
                    File.Move(temporaryPath, previewPath);
                    return new AcquisitionPreviewFile(stem, previewPath);
                }
                catch (IOException) when (File.Exists(previewPath))
                {
                    DeleteIfExists(temporaryPath);
                    continue;
                }
                catch
                {
                    DeleteIfExists(temporaryPath);
                    throw;
                }
            }
        }

        throw new IOException("Çakışmasız preview dosya adı ayrılamadı.");
    }

    public string SaveRawBesidePreview(RawGray16Frame rawFrame, string previewPath)
    {
        ArgumentNullException.ThrowIfNull(rawFrame);
        EnsureContainedExistingFile(previewPath);
        var fullPreviewPath = Path.GetFullPath(previewPath);
        if (!Path.GetExtension(fullPreviewPath).Equals(".bmp", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(fullPreviewPath).Equals(".tif", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(fullPreviewPath).Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Gray16 TIFF yalnız doğrulanmış BMP/TIFF preview yanına kaydedilebilir.");
        var previewStem = Path.GetFileNameWithoutExtension(fullPreviewPath);
        if (previewStem.EndsWith(".preview", StringComparison.OrdinalIgnoreCase))
            previewStem = previewStem[..^".preview".Length];
        var rawPath = Path.Combine(
            Path.GetDirectoryName(fullPreviewPath)!,
            previewStem + SourceTiffExtension);

        lock (_writeGate)
        {
            if (File.Exists(rawPath))
            {
                using var existingStream = File.OpenRead(rawPath);
                var existing = Gray16TiffCodec.Read(existingStream);
                EnsureSameRawIdentity(existing, rawFrame);
                return rawPath;
            }

            var temporaryPath = rawPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = CreateWriteStream(temporaryPath))
                {
                    Gray16TiffCodec.Write(stream, rawFrame);
                    stream.Flush(flushToDisk: true);
                }
                using (var verifyStream = File.OpenRead(temporaryPath))
                {
                    var verified = Gray16TiffCodec.Read(verifyStream);
                    EnsureSameRawIdentity(verified, rawFrame);
                }
                File.Move(temporaryPath, rawPath);
                return rawPath;
            }
            catch
            {
                DeleteIfExists(temporaryPath);
                throw;
            }
        }
    }

    private AcquisitionArtifactFiles SaveCore(
        RawGray16Frame rawFrame,
        byte[] previewBytes,
        string previewExtension,
        string requestedFileStem,
        byte[]? metadataBytes,
        string? metadataExtension)
    {
        ArgumentNullException.ThrowIfNull(rawFrame);
        ArgumentNullException.ThrowIfNull(previewBytes);
        if (previewBytes.Length == 0)
            throw new ArgumentException("Görüntü önizlemesi boş olamaz.", nameof(previewBytes));
        var extension = NormalizeAndValidatePreview(previewBytes, previewExtension);
        var baseStem = SanitizeFileStem(requestedFileStem);

        lock (_writeGate)
        {
            for (var attempt = 0; attempt < MaxCollisionAttempts; attempt++)
            {
                var stem = CreateCandidateStem(baseStem, rawFrame.AcquisitionId, attempt);
                var rawPath = Path.Combine(_baseFolder, stem + SourceTiffExtension);
                var previewPath = GetPreviewPath(stem, extension, sourceIncluded: true);
                var legacyRawPath = Path.Combine(_baseFolder, stem + LegacyRawExtension);
                var metadataPath = metadataExtension is null
                    ? null
                    : Path.Combine(_baseFolder, stem + metadataExtension);
                if (File.Exists(previewPath) || File.Exists(rawPath) || File.Exists(legacyRawPath) ||
                    metadataPath is not null && File.Exists(metadataPath)) continue;

                try
                {
                    WriteVerifiedArtifacts(
                        rawFrame,
                        previewBytes,
                        previewPath,
                        rawPath,
                        metadataBytes,
                        metadataPath);
                    return new AcquisitionArtifactFiles(stem, previewPath, rawPath, metadataPath);
                }
                catch (IOException) when (
                    File.Exists(previewPath) ||
                    File.Exists(rawPath) ||
                    metadataPath is not null && File.Exists(metadataPath))
                {
                    // Another process won this exact name; retry with the
                    // deterministic acquisition-id suffix.
                }
            }
        }

        throw new IOException("Çakışmasız acquisition dosya adı ayrılamadı.");
    }

    public RawGray16Frame LoadRaw(string path)
    {
        EnsureContainedExistingFile(path);
        using var stream = File.OpenRead(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tif" or ".tiff" => Gray16TiffCodec.Read(stream),
            LegacyRawExtension => Gray16RawFileCodec.Read(stream),
            _ => throw new InvalidDataException("Gray16 kaynak dosyası TIFF veya desteklenen eski XRaySystem raw biçiminde değil.")
        };
    }

    public byte[] LoadPreview(string path)
    {
        EnsureContainedExistingFile(path);
        return File.ReadAllBytes(path);
    }

    public byte[] LoadMetadata(string path)
    {
        EnsureContainedExistingFile(path);
        var bytes = File.ReadAllBytes(path);
        using var _ = JsonDocument.Parse(bytes);
        return bytes;
    }

    private void WriteVerifiedArtifacts(
        RawGray16Frame rawFrame,
        byte[] previewBytes,
        string previewPath,
        string rawPath,
        byte[]? metadataBytes,
        string? metadataPath)
    {
        if (string.Equals(
                Path.GetFullPath(previewPath),
                Path.GetFullPath(rawPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            WriteVerifiedTiffSourceWithMetadata(rawFrame, rawPath, metadataBytes, metadataPath);
            return;
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var previewTemp = previewPath + "." + transactionId + ".tmp";
        var rawTemp = rawPath + "." + transactionId + ".tmp";
        var metadataTemp = metadataPath is null ? null : metadataPath + "." + transactionId + ".tmp";
        var rawPromoted = false;
        var previewPromoted = false;
        var metadataPromoted = false;
        try
        {
            WriteBytes(previewTemp, previewBytes);
            _faultInjector?.Invoke(AcquisitionArtifactWriteStage.PreviewTempFlushed);
            if (metadataBytes is not null && metadataTemp is not null)
            {
                WriteBytes(metadataTemp, metadataBytes);
                using var _ = JsonDocument.Parse(File.ReadAllBytes(metadataTemp));
                _faultInjector?.Invoke(AcquisitionArtifactWriteStage.MetadataTempFlushed);
            }
            using (var stream = CreateWriteStream(rawTemp))
            {
                Gray16TiffCodec.Write(stream, rawFrame);
                stream.Flush(flushToDisk: true);
            }

            using (var verify = File.OpenRead(rawTemp))
            {
                var verified = Gray16TiffCodec.Read(verify);
                if (verified.AcquisitionId != rawFrame.AcquisitionId ||
                    verified.Width != rawFrame.Width ||
                    verified.Height != rawFrame.Height ||
                    verified.Operation != rawFrame.Operation ||
                    verified.SnapFrameMode != rawFrame.SnapFrameMode)
                    throw new InvalidDataException("Yazılan Gray16 TIFF acquisition metadata doğrulaması başarısız.");
            }
            _faultInjector?.Invoke(AcquisitionArtifactWriteStage.RawTempVerified);

            File.Move(rawTemp, rawPath);
            rawPromoted = true;
            _faultInjector?.Invoke(AcquisitionArtifactWriteStage.RawPromoted);
            File.Move(previewTemp, previewPath);
            previewPromoted = true;
            _faultInjector?.Invoke(AcquisitionArtifactWriteStage.PreviewPromoted);
            if (metadataTemp is not null && metadataPath is not null)
            {
                File.Move(metadataTemp, metadataPath);
                metadataPromoted = true;
                _faultInjector?.Invoke(AcquisitionArtifactWriteStage.MetadataPromoted);
            }
        }
        catch
        {
            DeleteIfExists(previewTemp);
            DeleteIfExists(rawTemp);
            if (metadataTemp is not null) DeleteIfExists(metadataTemp);
            if (rawPromoted) DeleteIfExists(rawPath);
            if (previewPromoted) DeleteIfExists(previewPath);
            if (metadataPromoted && metadataPath is not null) DeleteIfExists(metadataPath);
            throw;
        }
    }

    private void WriteVerifiedTiffSourceWithMetadata(
        RawGray16Frame rawFrame,
        string sourcePath,
        byte[]? metadataBytes,
        string? metadataPath)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var sourceTemp = sourcePath + "." + transactionId + ".tmp";
        var metadataTemp = metadataPath is null ? null : metadataPath + "." + transactionId + ".tmp";
        var sourcePromoted = false;
        var metadataPromoted = false;
        try
        {
            if (metadataBytes is not null && metadataTemp is not null)
            {
                WriteBytes(metadataTemp, metadataBytes);
                using var _ = JsonDocument.Parse(File.ReadAllBytes(metadataTemp));
                _faultInjector?.Invoke(AcquisitionArtifactWriteStage.MetadataTempFlushed);
            }
            using (var stream = CreateWriteStream(sourceTemp))
            {
                Gray16TiffCodec.Write(stream, rawFrame);
                stream.Flush(flushToDisk: true);
            }
            using (var verify = File.OpenRead(sourceTemp))
            {
                var verified = Gray16TiffCodec.Read(verify);
                EnsureSameRawIdentity(verified, rawFrame);
            }
            _faultInjector?.Invoke(AcquisitionArtifactWriteStage.RawTempVerified);

            File.Move(sourceTemp, sourcePath);
            sourcePromoted = true;
            _faultInjector?.Invoke(AcquisitionArtifactWriteStage.RawPromoted);
            if (metadataTemp is not null && metadataPath is not null)
            {
                File.Move(metadataTemp, metadataPath);
                metadataPromoted = true;
                _faultInjector?.Invoke(AcquisitionArtifactWriteStage.MetadataPromoted);
            }
        }
        catch
        {
            DeleteIfExists(sourceTemp);
            if (metadataTemp is not null) DeleteIfExists(metadataTemp);
            if (sourcePromoted) DeleteIfExists(sourcePath);
            if (metadataPromoted && metadataPath is not null) DeleteIfExists(metadataPath);
            throw;
        }
    }

    private void EnsureContainedExistingFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_baseFolder, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Görüntü yolu acquisition storage klasörü dışında.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Acquisition dosyası bulunamadı.", fullPath);
    }

    private static string CreateCandidateStem(string baseStem, Guid acquisitionId, int attempt) =>
        attempt switch
        {
            0 => baseStem,
            1 => $"{baseStem}_{acquisitionId:N}",
            _ => $"{baseStem}_{acquisitionId:N}_{attempt:00}"
        };

    private static string NormalizeAndValidatePreview(byte[] bytes, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            throw new ArgumentException("Önizleme uzantısı boş olamaz.", nameof(extension));
        var normalized = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        switch (normalized)
        {
            case ".bmp" when bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M':
                return normalized;
            case ".tif" or ".tiff" when bytes.Length >= 4 &&
                ((bytes[0] == (byte)'I' && bytes[1] == (byte)'I' && bytes[2] == 42 && bytes[3] == 0) ||
                 (bytes[0] == (byte)'M' && bytes[1] == (byte)'M' && bytes[2] == 0 && bytes[3] == 42)):
                return normalized;
            case ".bmp":
                throw new InvalidDataException("Önizleme geçerli BMP imzası taşımıyor.");
            case ".tif" or ".tiff":
                throw new InvalidDataException("Önizleme geçerli TIFF imzası taşımıyor.");
            default:
                throw new NotSupportedException("Yalnız BMP veya TIFF türetilmiş görüntü saklanabilir.");
        }
    }

    private static string SanitizeFileStem(string fileStem)
    {
        if (string.IsNullOrWhiteSpace(fileStem))
            throw new ArgumentException("Dosya adı boş olamaz.", nameof(fileStem));
        var stem = Path.GetFileName(fileStem.Trim());
        foreach (var knownExtension in new[] { ".xraymeta.json", ".preview.tiff", ".source.tiff", ".xrayraw", ".tiff", ".tif", ".bmp" })
        {
            if (!stem.EndsWith(knownExtension, StringComparison.OrdinalIgnoreCase)) continue;
            stem = stem[..^knownExtension.Length];
            break;
        }
        foreach (var invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(stem) || stem is "." or "..")
            throw new ArgumentException("Dosya adı geçersiz.", nameof(fileStem));
        return stem;
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        using var stream = CreateWriteStream(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureSameRawIdentity(RawGray16Frame actual, RawGray16Frame expected)
    {
        if (actual.AcquisitionId != expected.AcquisitionId ||
            actual.Width != expected.Width ||
            actual.Height != expected.Height ||
            actual.Operation != expected.Operation ||
            actual.SnapFrameMode != expected.SnapFrameMode)
            throw new InvalidDataException("Gray16 TIFF acquisition kimliği preview kaydıyla uyuşmuyor.");
    }

    private string GetPreviewPath(string stem, string extension, bool sourceIncluded) =>
        Path.Combine(
            _baseFolder,
            extension is ".tif" or ".tiff"
                ? sourceIncluded
                    ? stem + SourceTiffExtension
                    : stem + ".preview" + extension
                : stem + extension);

    private static FileStream CreateWriteStream(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 1024 * 1024,
        FileOptions.WriteThrough);

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
