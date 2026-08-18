using System.IO;
using System.Threading.Tasks;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Mobile.Services;

public class MauiWindowLevelRenderer
{
    public byte[] RenderToBmp(RawGray16Frame frame, int windowCenter, int windowWidth)
    {
        int width = frame.Width;
        int height = frame.Height;
        ushort[] pixels = frame.Pixels;

        // Hesaplamalar (Basit Window/Level)
        double minVal = windowCenter - (windowWidth / 2.0);
        double maxVal = windowCenter + (windowWidth / 2.0);
        if (minVal < 0) minVal = 0;
        if (maxVal > 65535) maxVal = 65535;

        double range = maxVal - minVal;
        if (range <= 0) range = 1;

        // 8-bit piksel verisini oluştur (BMP satırları padding gerektirir)
        int rowSize = (width + 3) & ~3; // 4'ün katı olmalı
        int pixelDataSize = rowSize * height;
        byte[] bmpPixelData = new byte[pixelDataSize];

        for (int y = 0; y < height; y++)
        {
            // BMP resimleri alttan üste (bottom-up) kaydedilir, o yüzden satırı ters hesaplıyoruz.
            int destRow = (height - 1 - y) * rowSize;
            int srcRow = y * width;

            for (int x = 0; x < width; x++)
            {
                ushort val = pixels[srcRow + x];
                int mapped;
                if (val <= minVal) mapped = 0;
                else if (val >= maxVal) mapped = 255;
                else mapped = (int)(((val - minVal) / range) * 255.0);

                bmpPixelData[destRow + x] = (byte)mapped;
            }
        }

        return CreateBmpFile(width, height, bmpPixelData);
    }

    private byte[] CreateBmpFile(int width, int height, byte[] pixelData)
    {
        int paletteSize = 256 * 4; // 8-bit indexed color palette
        int fileHeaderSize = 14;
        int infoHeaderSize = 40;
        int totalSize = fileHeaderSize + infoHeaderSize + paletteSize + pixelData.Length;

        using var ms = new MemoryStream(totalSize);
        using var bw = new BinaryWriter(ms);

        // BITMAPFILEHEADER
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(totalSize);
        bw.Write(0); // Reserved
        bw.Write(fileHeaderSize + infoHeaderSize + paletteSize); // Offset to pixel data

        // BITMAPINFOHEADER
        bw.Write(infoHeaderSize);
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1); // Color planes
        bw.Write((short)8); // Bits per pixel
        bw.Write(0); // Compression (0 = none)
        bw.Write(pixelData.Length);
        bw.Write(2835); // Horizontal resolution (72dpi)
        bw.Write(2835); // Vertical resolution
        bw.Write(256); // Colors in palette
        bw.Write(256); // Important colors

        // Color Palette (Grayscale)
        for (int i = 0; i < 256; i++)
        {
            bw.Write((byte)i); // Blue
            bw.Write((byte)i); // Green
            bw.Write((byte)i); // Red
            bw.Write((byte)0); // Reserved
        }

        // Pixel Data
        bw.Write(pixelData);

        return ms.ToArray();
    }
}
