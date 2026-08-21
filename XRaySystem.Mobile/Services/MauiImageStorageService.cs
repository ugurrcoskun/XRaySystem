using System;
using System.IO;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;
using Microsoft.Maui.Storage;

namespace XRaySystem.Mobile.Services;

public sealed record MobileSnapshotFiles(string PreviewPath, string RawPath);

public class MauiImageStorageService
{
    private readonly string _baseFolder;

    public MauiImageStorageService()
    {
        _baseFolder = Path.Combine(FileSystem.AppDataDirectory, "Images");
        Directory.CreateDirectory(_baseFolder);
    }

    public MobileSnapshotFiles SaveSnapshot(byte[] previewBmp, RawGray16Frame rawFrame, string fileStem)
    {
        ArgumentNullException.ThrowIfNull(previewBmp);
        ArgumentNullException.ThrowIfNull(rawFrame);
        if (previewBmp.Length == 0) throw new ArgumentException("BMP önizleme boş olamaz.", nameof(previewBmp));

        var safeStem = SanitizeFileStem(fileStem);
        var previewPath = Path.Combine(_baseFolder, $"{safeStem}.bmp");
        var rawPath = Path.Combine(_baseFolder, $"{safeStem}.xrayraw");
        if (File.Exists(previewPath) || File.Exists(rawPath))
        {
            safeStem = $"{safeStem}_{Guid.NewGuid():N}";
            previewPath = Path.Combine(_baseFolder, $"{safeStem}.bmp");
            rawPath = Path.Combine(_baseFolder, $"{safeStem}.xrayraw");
        }

        var previewTemp = $"{previewPath}.{Guid.NewGuid():N}.tmp";
        var rawTemp = $"{rawPath}.{Guid.NewGuid():N}.tmp";
        var rawMoved = false;
        try
        {
            WriteBytes(previewTemp, previewBmp);
            using (var stream = CreateWriteStream(rawTemp))
            {
                Gray16RawFileCodec.Write(stream, rawFrame);
                stream.Flush(flushToDisk: true);
            }

            // Taşımadan önce diskteki raw verinin okunabildiğini ve hash'ini doğrula.
            using (var verify = File.OpenRead(rawTemp))
                _ = Gray16RawFileCodec.Read(verify);

            File.Move(rawTemp, rawPath);
            rawMoved = true;
            File.Move(previewTemp, previewPath);
            return new MobileSnapshotFiles(previewPath, rawPath);
        }
        catch
        {
            DeleteIfExists(previewTemp);
            DeleteIfExists(rawTemp);
            if (rawMoved) DeleteIfExists(rawPath);
            throw;
        }
    }

    public byte[] LoadBmp(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Görüntü bulunamadı: {filePath}");
        
        return File.ReadAllBytes(filePath);
    }

    public RawGray16Frame LoadRaw(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Raw görüntü bulunamadı: {filePath}");

        using var stream = File.OpenRead(filePath);
        return Gray16RawFileCodec.Read(stream);
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        using var stream = CreateWriteStream(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream CreateWriteStream(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 1024 * 1024,
        FileOptions.WriteThrough);

    private static string SanitizeFileStem(string fileStem)
    {
        if (string.IsNullOrWhiteSpace(fileStem)) throw new ArgumentException("Dosya adı boş olamaz.", nameof(fileStem));
        var stem = Path.GetFileNameWithoutExtension(fileStem.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(stem) || stem is "." or "..")
            throw new ArgumentException("Dosya adı geçersiz.", nameof(fileStem));
        return stem;
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Ana hatayı gölgelememek için cleanup hatasını yut. */ }
    }
}
