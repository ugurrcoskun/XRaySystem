namespace XRaySystem.Infrastructure.Imaging;

public sealed record Gray8DecodedImage(byte[] Pixels, int Width, int Height);

/// <summary>
/// Decodes the uncompressed, palette-based Gray8 BMP format produced by
/// <see cref="Gray8BmpEncoder"/>. The canonical result is top-left oriented.
/// </summary>
public static class Gray8BmpDecoder
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PaletteEntries = 256;
    private const int PaletteSize = PaletteEntries * 4;
    private const int MinimumPixelOffset = FileHeaderSize + InfoHeaderSize + PaletteSize;
    private const int MaxPixelCount = 100_000_000;

    public static Gray8DecodedImage Decode(byte[] bmpBytes)
    {
        ArgumentNullException.ThrowIfNull(bmpBytes);
        if (bmpBytes.Length < MinimumPixelOffset)
            throw new InvalidDataException("Gray8 BMP başlığı eksik veya yarım.");

        using var stream = new MemoryStream(bmpBytes, writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != (byte)'B' || reader.ReadByte() != (byte)'M')
            throw new InvalidDataException("Görüntü BMP biçiminde değil.");

        var declaredFileSize = reader.ReadInt32();
        _ = reader.ReadInt32(); // reserved
        var pixelOffset = reader.ReadInt32();
        var infoHeaderSize = reader.ReadInt32();
        var width = reader.ReadInt32();
        var storedHeight = reader.ReadInt32();
        var planes = reader.ReadInt16();
        var bitsPerPixel = reader.ReadInt16();
        var compression = reader.ReadInt32();
        var declaredPixelBytes = reader.ReadInt32();
        _ = reader.ReadInt32(); // horizontal DPI
        _ = reader.ReadInt32(); // vertical DPI
        var colorsUsed = reader.ReadInt32();
        _ = reader.ReadInt32(); // important colors

        if (declaredFileSize != bmpBytes.Length || infoHeaderSize != InfoHeaderSize ||
            planes != 1 || bitsPerPixel != 8 || compression != 0)
            throw new InvalidDataException("Yalnız sıkıştırılmamış 8-bit Gray BMP desteklenir.");
        if (width <= 0 || storedHeight == 0 || storedHeight == int.MinValue)
            throw new InvalidDataException("Gray8 BMP boyutları geçersiz.");

        var height = Math.Abs(storedHeight);
        int pixelCount;
        int rowSize;
        int expectedPixelBytes;
        try
        {
            pixelCount = checked(width * height);
            rowSize = checked((width + 3) & ~3);
            expectedPixelBytes = checked(rowSize * height);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Gray8 BMP boyutları taşmaya neden oluyor.", ex);
        }

        if (pixelCount > MaxPixelCount || pixelOffset < MinimumPixelOffset ||
            pixelOffset > bmpBytes.Length - expectedPixelBytes ||
            declaredPixelBytes != expectedPixelBytes ||
            colorsUsed is not (0 or PaletteEntries))
            throw new InvalidDataException("Gray8 BMP boyut veya offset bilgisi geçersiz.");

        stream.Position = FileHeaderSize + InfoHeaderSize;
        for (var index = 0; index < PaletteEntries; index++)
        {
            var blue = reader.ReadByte();
            var green = reader.ReadByte();
            var red = reader.ReadByte();
            _ = reader.ReadByte();
            if (blue != index || green != index || red != index)
                throw new InvalidDataException("BMP paleti doğrusal gri ton paleti değil.");
        }

        var pixels = new byte[pixelCount];
        stream.Position = pixelOffset;
        var bottomUp = storedHeight > 0;
        for (var storedRow = 0; storedRow < height; storedRow++)
        {
            var targetY = bottomUp ? height - 1 - storedRow : storedRow;
            var read = stream.Read(pixels, targetY * width, width);
            if (read != width) throw new EndOfStreamException("Gray8 BMP piksel verisi yarım.");
            stream.Position += rowSize - width;
        }

        return new Gray8DecodedImage(pixels, width, height);
    }

    public static ushort[] ExpandToGray16(byte[] gray8Pixels)
    {
        ArgumentNullException.ThrowIfNull(gray8Pixels);
        var gray16 = new ushort[gray8Pixels.Length];
        for (var index = 0; index < gray8Pixels.Length; index++)
            gray16[index] = (ushort)(gray8Pixels[index] * 257);
        return gray16;
    }
}
