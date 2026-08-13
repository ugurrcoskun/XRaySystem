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

    public ImageStorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _baseFolder = Path.Combine(appData, "XRaySystem", "Images");
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

        // TIFF Encoder – sıkıştırmasız, kayıpsız
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.None };
        encoder.Frames.Add(BitmapFrame.Create(frozenBitmap));

        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);

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
