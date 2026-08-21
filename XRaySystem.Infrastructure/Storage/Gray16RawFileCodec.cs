using System.Security.Cryptography;
using System.Text;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Storage;

/// <summary>
/// XRaySystem'e ait sürümlü, platformdan bağımsız Gray16 dosya biçimi.
/// Piksel verisi little-endian saklanır ve SHA-256 ile doğrulanır.
/// </summary>
public static class Gray16RawFileCodec
{
    private static readonly byte[] Magic = "XRAYRAW1"u8.ToArray();
    private const int Version = 1;
    private const int HashLength = 32;
    private const int MaxPixelCount = 100_000_000;
    private const int MaxStringBytes = 4096;

    public static void Write(Stream destination, RawGray16Frame frame)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(frame);
        if (!destination.CanWrite) throw new ArgumentException("Hedef stream yazılabilir olmalıdır.", nameof(destination));

        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(frame.Width);
        writer.Write(frame.Height);
        writer.Write(frame.StrideBytes);
        writer.Write((int)frame.PixelFormat);
        writer.Write((int)frame.Kind);
        writer.Write(frame.Timestamp.Ticks);
        writer.Write(checked((short)frame.Timestamp.Offset.TotalMinutes));
        writer.Write(frame.AcquisitionId.ToByteArray());
        writer.Write(frame.Settings.TubeCurrent);
        writer.Write(frame.Settings.Voltage);
        writer.Write(frame.Settings.ExposureTime);
        writer.Write((int)frame.Settings.Profile);
        writer.Write(frame.SourceStrideBytes ?? -1);
        WriteString(writer, frame.SourceMarker);
        WriteString(writer, frame.SourceEndian);
        WriteString(writer, frame.SourceOrientation);
        writer.Write(frame.PixelCount);

        var pixelBytes = new byte[checked(frame.PixelCount * sizeof(ushort))];
        Buffer.BlockCopy(frame.Pixels, 0, pixelBytes, 0, pixelBytes.Length);
        if (!BitConverter.IsLittleEndian) SwapBytePairs(pixelBytes);

        writer.Write(pixelBytes);
        writer.Write(SHA256.HashData(pixelBytes));
        writer.Flush();
    }

    public static RawGray16Frame Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Kaynak stream okunabilir olmalıdır.", nameof(source));

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        if (!ReadExact(reader, Magic.Length).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Dosya XRaySystem Gray16 raw biçiminde değil.");

        var version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"Desteklenmeyen XRaySystem raw sürümü: {version}.");

        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var strideBytes = reader.ReadInt32();
        var pixelFormat = (Gray16PixelFormat)reader.ReadInt32();
        var kind = (FrameKind)reader.ReadInt32();
        var timestampTicks = reader.ReadInt64();
        var offsetMinutes = reader.ReadInt16();
        if (offsetMinutes is < -840 or > 840)
            throw new InvalidDataException("Raw dosyasındaki zaman dilimi ofseti geçersiz.");

        var acquisitionId = new Guid(ReadExact(reader, 16));
        var settings = new AcquisitionSettings(
            reader.ReadDouble(),
            reader.ReadDouble(),
            reader.ReadDouble(),
            (AcquisitionProfile)reader.ReadInt32());
        var sourceStride = reader.ReadInt32();
        var sourceMarker = ReadString(reader, required: true)!;
        var sourceEndian = ReadString(reader, required: false);
        var sourceOrientation = ReadString(reader, required: false);
        var storedPixelCount = reader.ReadInt32();

        int expectedPixelCount;
        try { expectedPixelCount = checked(width * height); }
        catch (OverflowException ex) { throw new InvalidDataException("Raw görüntü boyutları geçersiz.", ex); }
        if (width <= 0 || height <= 0 || expectedPixelCount > MaxPixelCount || storedPixelCount != expectedPixelCount)
            throw new InvalidDataException("Raw görüntü boyutları veya piksel sayısı geçersiz.");

        var pixelBytes = ReadExact(reader, checked(expectedPixelCount * sizeof(ushort)));
        var storedHash = ReadExact(reader, HashLength);
        var actualHash = SHA256.HashData(pixelBytes);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, actualHash))
            throw new InvalidDataException("Raw görüntü bütünlük doğrulaması başarısız.");
        if (source.ReadByte() != -1)
            throw new InvalidDataException("Raw dosyasının sonunda beklenmeyen veri var.");

        if (!BitConverter.IsLittleEndian) SwapBytePairs(pixelBytes);
        var pixels = new ushort[expectedPixelCount];
        Buffer.BlockCopy(pixelBytes, 0, pixels, 0, pixelBytes.Length);

        DateTimeOffset timestamp;
        try { timestamp = new DateTimeOffset(timestampTicks, TimeSpan.FromMinutes(offsetMinutes)); }
        catch (ArgumentException ex) { throw new InvalidDataException("Raw dosyasındaki zaman bilgisi geçersiz.", ex); }

        try
        {
            return new RawGray16Frame(
                pixels,
                width,
                height,
                strideBytes,
                pixelFormat,
                kind,
                timestamp,
                acquisitionId,
                settings,
                sourceMarker,
                sourceStride >= 0 ? sourceStride : null,
                sourceEndian,
                sourceOrientation);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Raw görüntü metadata doğrulaması başarısız.", ex);
        }
    }

    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
            throw new InvalidDataException($"Raw metadata metni {MaxStringBytes} byte sınırını aşıyor.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadString(BinaryReader reader, bool required)
    {
        var length = reader.ReadInt32();
        if (length == -1)
        {
            if (required) throw new InvalidDataException("Zorunlu raw metadata metni eksik.");
            return null;
        }
        if (length is < 0 or > MaxStringBytes)
            throw new InvalidDataException("Raw metadata metin uzunluğu geçersiz.");

        var value = Encoding.UTF8.GetString(ReadExact(reader, length));
        if (required && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Zorunlu raw metadata metni boş.");
        return value;
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException("Raw görüntü dosyası yarım veya bozuk.");
        return bytes;
    }

    private static void SwapBytePairs(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i += 2)
            (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
    }
}
