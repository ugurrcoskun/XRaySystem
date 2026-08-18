using System;
using System.IO;

namespace XRaySystem.Mobile.Services;

public class MauiImageStorageService
{
    private readonly string _baseFolder;

    public MauiImageStorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _baseFolder = Path.Combine(appData, "XRaySystem", "MobileImages");
        Directory.CreateDirectory(_baseFolder);
    }

    public string SaveBmp(byte[] bmpData, string fileName)
    {
        var fullPath = Path.Combine(_baseFolder, fileName);
        if (File.Exists(fullPath))
            fullPath = Path.Combine(_baseFolder, $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}.bmp");

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(bmpData);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
        return fullPath;
    }

    public byte[] LoadBmp(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Görüntü bulunamadı: {filePath}");
        
        return File.ReadAllBytes(filePath);
    }
}
