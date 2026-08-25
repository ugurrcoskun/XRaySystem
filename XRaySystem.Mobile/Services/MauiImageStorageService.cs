using System;
using System.IO;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;
using Microsoft.Maui.Storage;

namespace XRaySystem.Mobile.Services;

public sealed record MobileSnapshotFiles(string PreviewPath, string RawPath);

public class MauiImageStorageService
{
    private readonly AcquisitionArtifactStorage _storage;

    public MauiImageStorageService()
    {
        _storage = new AcquisitionArtifactStorage(
            Path.Combine(FileSystem.AppDataDirectory, "Images"));
    }

    public MobileSnapshotFiles SaveSnapshot(byte[] previewBmp, RawGray16Frame rawFrame, string fileStem)
    {
        var files = _storage.Save(rawFrame, previewBmp, ".bmp", fileStem);
        return new MobileSnapshotFiles(files.PreviewPath, files.RawPath);
    }

    public byte[] LoadBmp(string filePath)
    {
        return _storage.LoadPreview(filePath);
    }

    public RawGray16Frame LoadRaw(string filePath)
    {
        return _storage.LoadRaw(filePath);
    }
}
