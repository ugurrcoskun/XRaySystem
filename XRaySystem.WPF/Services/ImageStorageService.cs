using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XRaySystem.WPF.Services;

/// <summary>
/// Görüntüyü diske 16-bit TIFF olarak kaydeder / diskten okur.
/// WPF'e özgü tipler kullandığı için WPF projesinde bulunur.
/// </summary>
public interface IImageStorageService
{
    string SaveTiff(BitmapSource frozenBitmap, string fileName);
    BitmapSource LoadTiff(string filePath);
}

public class ImageStorageService : IImageStorageService
{
    private readonly string _baseFolder;

    public ImageStorageService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XRaySystem",
        "Images"))
    {
    }

    internal ImageStorageService(string baseFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseFolder);
        _baseFolder = Path.GetFullPath(baseFolder);
        Directory.CreateDirectory(_baseFolder);
    }

    /// <summary>
    /// Önceden dondurulmuş (Freeze) bir BitmapSource'u TIFF olarak diske kaydeder.
    /// Frozen BitmapSource her thread'den erişilebilir.
    /// </summary>
    public string SaveTiff(BitmapSource frozenBitmap, string fileName)
    {
        if (frozenBitmap == null)
            throw new ArgumentNullException(nameof(frozenBitmap));
        if (!frozenBitmap.IsFrozen)
            throw new InvalidOperationException("BitmapSource Freeze() edilmeden kaydedilemez.");

        var fullPath = Path.Combine(_baseFolder, fileName);
        if (File.Exists(fullPath))
            fullPath = Path.Combine(_baseFolder, $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}{Path.GetExtension(fileName)}");

        // TIFF Encoder – sıkıştırmasız, kayıpsız
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.None };
        encoder.Frames.Add(BitmapFrame.Create(frozenBitmap));

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
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }

            // Aynı klasördeki move, yarım TIFF'in nihai adla görünmesini engeller.
            File.Move(temporaryPath, fullPath);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }

        return fullPath;
    }

    public BitmapSource LoadTiff(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Görüntü dosyası bulunamadı: {filePath}");

        var decoder = new TiffBitmapDecoder(
            new Uri(filePath, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
