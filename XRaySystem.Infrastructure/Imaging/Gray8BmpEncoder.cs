using XRaySystem.Domain.ValueObjects;
using XRaySystem.Domain.WindowLevel;

namespace XRaySystem.Infrastructure.Imaging;

public sealed record Gray8BmpImage(byte[] Bytes, int Width, int Height);

public static class Gray8BmpEncoder
{
    public static byte[] Encode(
        RawGray16Frame frame,
        double windowCenter,
        double windowWidth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var gray8 = WindowLevelMapper.Map(
            frame.Pixels,
            windowCenter,
            windowWidth,
            cancellationToken);
        return EncodePixels(gray8, frame.Width, frame.Height);
    }

    public static Gray8BmpImage EncodePreview(
        RawGray16Frame frame,
        int maxWidth,
        int maxHeight,
        double windowCenter,
        double windowWidth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (maxWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        if (maxHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maxHeight));

        var scale = Math.Min(1.0, Math.Min(
            (double)maxWidth / frame.Width,
            (double)maxHeight / frame.Height));
        var width = Math.Max(1, (int)Math.Floor(frame.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(frame.Height * scale));
        var lut = WindowLevelMapper.BuildLut(windowCenter, windowWidth);
        var gray8 = new byte[checked(width * height)];

        for (var y = 0; y < height; y++)
        {
            if ((y & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();
            var sourceY = Math.Min(frame.Height - 1, (int)((long)y * frame.Height / height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(frame.Width - 1, (int)((long)x * frame.Width / width));
                gray8[y * width + x] = lut[frame.Pixels[sourceY * frame.Width + sourceX]];
            }
        }

        return new Gray8BmpImage(EncodePixels(gray8, width, height), width, height);
    }

    private static byte[] EncodePixels(byte[] gray8, int width, int height)
    {
        if (gray8.Length != checked(width * height))
            throw new ArgumentException("Gray8 piksel sayısı görüntü boyutlarıyla uyuşmuyor.", nameof(gray8));

        var rowSize = checked((width + 3) & ~3);
        var pixelDataSize = checked(rowSize * height);
        var paletteSize = 256 * 4;
        const int fileHeaderSize = 14;
        const int infoHeaderSize = 40;
        var pixelOffset = fileHeaderSize + infoHeaderSize + paletteSize;
        var totalSize = checked(pixelOffset + pixelDataSize);

        using var stream = new MemoryStream(totalSize);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(totalSize);
        writer.Write(0);
        writer.Write(pixelOffset);
        writer.Write(infoHeaderSize);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)8);
        writer.Write(0);
        writer.Write(pixelDataSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(256);
        writer.Write(256);

        for (var value = 0; value < 256; value++)
        {
            writer.Write((byte)value);
            writer.Write((byte)value);
            writer.Write((byte)value);
            writer.Write((byte)0);
        }

        var padding = rowSize - width;
        for (var y = height - 1; y >= 0; y--)
        {
            writer.Write(gray8, y * width, width);
            for (var index = 0; index < padding; index++) writer.Write((byte)0);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
