using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Storage;

/// <summary>
/// Reads and writes an interoperable, uncompressed TIFF 6.0 image whose single
/// sample is unsigned 16-bit grayscale (BlackIsZero). XRaySystem acquisition
/// metadata is stored in the standard ImageDescription tag; ordinary TIFF
/// readers can ignore it and still display the pixels.
/// </summary>
public static class Gray16TiffCodec
{
    private const ushort TiffMagic = 42;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private const int MetadataSchemaVersion = 1;
    private const int MaxPixelCount = 100_000_000;
    private const int MaxMetadataBytes = 256 * 1024;
    private const uint RowsPerStrip = 64;
    private const ushort EntryCount = 18;
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);

    public static void Write(Stream destination, RawGray16Frame frame)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(frame);
        if (!destination.CanWrite)
            throw new ArgumentException("Hedef stream yazılabilir olmalıdır.", nameof(destination));
        if (destination.CanSeek && destination.Position != 0)
            throw new ArgumentException("TIFF stream yazımı dosyanın başlangıcında başlamalıdır.", nameof(destination));
        if (frame.PixelCount > MaxPixelCount)
            throw new InvalidDataException("TIFF piksel sayısı güvenli sınırı aşıyor.");

        var pixelBytes = MemoryMarshal.AsBytes(frame.Pixels.AsSpan());
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Gray16 TIFF yazımı şu anda little-endian platform gerektiriyor.");

        var pixelHash = Convert.ToHexString(SHA256.HashData(pixelBytes));
        var metadata = new Gray16TiffMetadata(
            MetadataSchemaVersion,
            frame.AcquisitionId,
            frame.Timestamp,
            frame.Kind.ToString(),
            frame.Operation.ToString(),
            frame.SnapFrameMode?.ToString(),
            frame.Settings.TubeCurrent,
            frame.Settings.Voltage,
            frame.Settings.ExposureTime,
            frame.Settings.Profile.ToString(),
            frame.SourceMarker,
            frame.SourceStrideBytes,
            frame.SourceEndian,
            frame.SourceOrientation,
            pixelHash);
        var description = NullTerminate(JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataJsonOptions));
        if (description.Length > MaxMetadataBytes)
            throw new InvalidDataException("TIFF acquisition metadata güvenli boyut sınırını aşıyor.");
        if (description.AsSpan(0, description.Length - 1).IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0)
            throw new InvalidDataException("TIFF ImageDescription metadata ASCII olarak kodlanamadı.");

        var software = NullTerminate(Encoding.ASCII.GetBytes("XRaySystem Gray16 TIFF"));
        var dateTime = NullTerminate(Encoding.ASCII.GetBytes(
            frame.Timestamp.UtcDateTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)));
        var stripCount = checked((uint)((frame.Height + (int)RowsPerStrip - 1) / (int)RowsPerStrip));
        var stripOffsetsByteCount = stripCount > 1 ? checked(stripCount * sizeof(uint)) : 0;
        var stripByteCountsByteCount = stripCount > 1 ? checked(stripCount * sizeof(uint)) : 0;

        const uint ifdOffset = 8;
        var payloadOffset = Align4(checked(ifdOffset + sizeof(ushort) + EntryCount * 12u + sizeof(uint)));
        var descriptionOffset = payloadOffset;
        var softwareOffset = Align4(checked(descriptionOffset + (uint)description.Length));
        var dateTimeOffset = Align4(checked(softwareOffset + (uint)software.Length));
        var xResolutionOffset = Align4(checked(dateTimeOffset + (uint)dateTime.Length));
        var yResolutionOffset = checked(xResolutionOffset + 8u);
        var stripOffsetsOffset = Align4(checked(yResolutionOffset + 8u));
        var stripByteCountsOffset = Align4(checked(stripOffsetsOffset + stripOffsetsByteCount));
        var pixelDataOffset = Align4(checked(stripByteCountsOffset + stripByteCountsByteCount));
        var totalPixelBytes = checked((uint)(frame.PixelCount * sizeof(ushort)));
        _ = checked(pixelDataOffset + totalPixelBytes);

        using var writer = new BinaryWriter(destination, Encoding.ASCII, leaveOpen: true);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write(TiffMagic);
        writer.Write(ifdOffset);
        writer.Write(EntryCount);
        WriteLongEntry(writer, 256, checked((uint)frame.Width));
        WriteLongEntry(writer, 257, checked((uint)frame.Height));
        WriteShortEntry(writer, 258, 16);
        WriteShortEntry(writer, 259, 1); // Compression: none
        WriteShortEntry(writer, 262, 1); // PhotometricInterpretation: BlackIsZero
        WriteOffsetEntry(writer, 270, TypeAscii, checked((uint)description.Length), descriptionOffset);
        WriteOffsetOrInlineLongArrayEntry(writer, 273, stripCount, stripOffsetsOffset, pixelDataOffset);
        WriteShortEntry(writer, 274, 1); // Orientation: top-left
        WriteShortEntry(writer, 277, 1); // SamplesPerPixel
        WriteLongEntry(writer, 278, RowsPerStrip);
        WriteOffsetOrInlineLongArrayEntry(
            writer,
            279,
            stripCount,
            stripByteCountsOffset,
            GetStripByteCount(frame.Width, frame.Height, 0));
        WriteOffsetEntry(writer, 282, TypeRational, 1, xResolutionOffset);
        WriteOffsetEntry(writer, 283, TypeRational, 1, yResolutionOffset);
        WriteShortEntry(writer, 284, 1); // PlanarConfiguration: chunky
        WriteShortEntry(writer, 296, 2); // ResolutionUnit: inch
        WriteOffsetEntry(writer, 305, TypeAscii, checked((uint)software.Length), softwareOffset);
        WriteOffsetEntry(writer, 306, TypeAscii, checked((uint)dateTime.Length), dateTimeOffset);
        WriteShortEntry(writer, 339, 1); // SampleFormat: unsigned integer
        writer.Write(0u); // next IFD

        WriteAtOffset(writer, descriptionOffset, description);
        WriteAtOffset(writer, softwareOffset, software);
        WriteAtOffset(writer, dateTimeOffset, dateTime);
        PadTo(writer, xResolutionOffset);
        writer.Write(96u);
        writer.Write(1u);
        PadTo(writer, yResolutionOffset);
        writer.Write(96u);
        writer.Write(1u);

        if (stripCount > 1)
        {
            PadTo(writer, stripOffsetsOffset);
            for (uint strip = 0; strip < stripCount; strip++)
                writer.Write(checked(pixelDataOffset + GetStripPixelByteOffset(frame.Width, strip)));
            PadTo(writer, stripByteCountsOffset);
            for (uint strip = 0; strip < stripCount; strip++)
                writer.Write(GetStripByteCount(frame.Width, frame.Height, strip));
        }

        PadTo(writer, pixelDataOffset);
        destination.Write(pixelBytes);
        writer.Flush();
    }

    public static RawGray16Frame Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("Kaynak TIFF stream okunabilir ve seek destekli olmalıdır.", nameof(source));
        source.Position = 0;
        if (source.Length < 8)
            throw new EndOfStreamException("TIFF dosyası yarım.");

        Span<byte> header = stackalloc byte[8];
        ReadExact(source, header);
        if (header[0] != (byte)'I' || header[1] != (byte)'I')
            throw new InvalidDataException("Yalnız little-endian TIFF Gray16 kaynakları destekleniyor.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[2..4]) != TiffMagic)
            throw new InvalidDataException("Dosya geçerli TIFF imzası taşımıyor.");
        var ifdOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        EnsureRange(source, ifdOffset, sizeof(ushort));
        source.Position = ifdOffset;
        var entryCount = ReadUInt16(source);
        if (entryCount is 0 or > 256)
            throw new InvalidDataException("TIFF IFD kayıt sayısı geçersiz.");

        var entries = new Dictionary<ushort, TiffEntry>();
        Span<byte> entryBytes = stackalloc byte[12];
        for (var i = 0; i < entryCount; i++)
        {
            ReadExact(source, entryBytes);
            var entry = new TiffEntry(
                BinaryPrimitives.ReadUInt16LittleEndian(entryBytes[0..2]),
                BinaryPrimitives.ReadUInt16LittleEndian(entryBytes[2..4]),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBytes[4..8]),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBytes[8..12]));
            entries[entry.Tag] = entry;
        }

        var width = checked((int)GetScalar(entries, 256));
        var height = checked((int)GetScalar(entries, 257));
        int pixelCount;
        try { pixelCount = checked(width * height); }
        catch (OverflowException ex) { throw new InvalidDataException("TIFF boyutları geçersiz.", ex); }
        if (width <= 0 || height <= 0 || pixelCount > MaxPixelCount)
            throw new InvalidDataException("TIFF boyutları güvenli sınırlar dışında.");
        if (GetScalar(entries, 258) != 16 ||
            GetScalar(entries, 259) != 1 ||
            GetScalar(entries, 262) != 1 ||
            GetOptionalScalar(entries, 274, 1) != 1 ||
            GetScalar(entries, 277) != 1 ||
            GetOptionalScalar(entries, 284, 1) != 1 ||
            GetOptionalScalar(entries, 339, 1) != 1)
            throw new InvalidDataException("TIFF, sıkıştırmasız unsigned Gray16 BlackIsZero biçiminde değil.");

        var rowsPerStrip = GetScalar(entries, 278);
        if (rowsPerStrip == 0 || rowsPerStrip > int.MaxValue)
            throw new InvalidDataException("TIFF RowsPerStrip değeri geçersiz.");
        var expectedStripCount = checked((uint)((height + (long)rowsPerStrip - 1) / rowsPerStrip));
        var stripOffsets = GetLongValues(source, entries, 273);
        var stripByteCounts = GetLongValues(source, entries, 279);
        if (stripOffsets.Length != expectedStripCount || stripByteCounts.Length != expectedStripCount)
            throw new InvalidDataException("TIFF strip tablosu görüntü boyutlarıyla uyuşmuyor.");

        var descriptionBytes = GetAsciiBytes(source, entries, 270, required: true);
        if (descriptionBytes.Length > MaxMetadataBytes)
            throw new InvalidDataException("TIFF acquisition metadata güvenli boyut sınırını aşıyor.");
        var metadata = JsonSerializer.Deserialize<Gray16TiffMetadata>(descriptionBytes, MetadataJsonOptions)
            ?? throw new InvalidDataException("TIFF acquisition metadata boş.");
        ValidateMetadata(metadata);

        var pixels = new ushort[pixelCount];
        var destinationBytes = MemoryMarshal.AsBytes(pixels.AsSpan());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var destinationOffset = 0;
        for (uint strip = 0; strip < expectedStripCount; strip++)
        {
            var firstRow = checked((long)strip * rowsPerStrip);
            var rows = Math.Min((long)rowsPerStrip, height - firstRow);
            var expectedBytes = checked((int)(rows * width * sizeof(ushort)));
            if (stripByteCounts[strip] != expectedBytes)
                throw new InvalidDataException("TIFF strip byte count değeri görüntü boyutlarıyla uyuşmuyor.");
            EnsureRange(source, stripOffsets[strip], stripByteCounts[strip]);
            source.Position = stripOffsets[strip];
            var target = destinationBytes.Slice(destinationOffset, expectedBytes);
            ReadExact(source, target);
            hash.AppendData(target);
            destinationOffset += expectedBytes;
        }
        if (destinationOffset != destinationBytes.Length)
            throw new InvalidDataException("TIFF piksel verisi eksik.");
        if (!BitConverter.IsLittleEndian)
            SwapBytePairs(destinationBytes);

        byte[] expectedHash;
        try { expectedHash = Convert.FromHexString(metadata.PixelSha256); }
        catch (FormatException ex) { throw new InvalidDataException("TIFF piksel hash biçimi geçersiz.", ex); }
        var actualHash = hash.GetHashAndReset();
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            throw new InvalidDataException("TIFF piksel bütünlük doğrulaması başarısız.");

        if (!Enum.TryParse<FrameKind>(metadata.Kind, false, out var kind) || !Enum.IsDefined(kind) ||
            !Enum.TryParse<AcquisitionOperation>(metadata.Operation, false, out var operation) || !Enum.IsDefined(operation) ||
            !Enum.TryParse<AcquisitionProfile>(metadata.Profile, false, out var profile) || !Enum.IsDefined(profile))
            throw new InvalidDataException("TIFF acquisition enum metadata bilgisi geçersiz.");
        SnapFrameMode? snapFrameMode = null;
        if (metadata.SnapFrameMode is not null)
        {
            if (!Enum.TryParse<SnapFrameMode>(metadata.SnapFrameMode, false, out var parsed) || !Enum.IsDefined(parsed))
                throw new InvalidDataException("TIFF SNAP frame modu geçersiz.");
            snapFrameMode = parsed;
        }

        try
        {
            return new RawGray16Frame(
                pixels,
                width,
                height,
                checked(width * sizeof(ushort)),
                Gray16PixelFormat.Gray16LittleEndian,
                kind,
                metadata.Timestamp,
                metadata.AcquisitionId,
                new AcquisitionSettings(
                    metadata.TubeCurrent,
                    metadata.Voltage,
                    metadata.ExposureTime,
                    profile),
                metadata.SourceMarker,
                metadata.SourceStrideBytes,
                metadata.SourceEndian,
                metadata.SourceOrientation,
                operation,
                snapFrameMode);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("TIFF acquisition metadata doğrulaması başarısız.", ex);
        }
    }

    private static void ValidateMetadata(Gray16TiffMetadata metadata)
    {
        if (metadata.SchemaVersion != MetadataSchemaVersion ||
            metadata.AcquisitionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(metadata.Kind) ||
            string.IsNullOrWhiteSpace(metadata.Operation) ||
            string.IsNullOrWhiteSpace(metadata.Profile) ||
            string.IsNullOrWhiteSpace(metadata.SourceMarker) ||
            string.IsNullOrWhiteSpace(metadata.PixelSha256) ||
            !double.IsFinite(metadata.TubeCurrent) ||
            !double.IsFinite(metadata.Voltage) ||
            !double.IsFinite(metadata.ExposureTime))
            throw new InvalidDataException("TIFF acquisition metadata eksik veya geçersiz.");
    }

    private static uint GetScalar(IReadOnlyDictionary<ushort, TiffEntry> entries, ushort tag)
    {
        if (!entries.TryGetValue(tag, out var entry) || entry.Count != 1)
            throw new InvalidDataException($"TIFF zorunlu tag {tag} eksik veya geçersiz.");
        return entry.Type switch
        {
            TypeShort => entry.ValueOrOffset & 0xFFFF,
            TypeLong => entry.ValueOrOffset,
            _ => throw new InvalidDataException($"TIFF tag {tag} veri tipi desteklenmiyor.")
        };
    }

    private static uint GetOptionalScalar(
        IReadOnlyDictionary<ushort, TiffEntry> entries,
        ushort tag,
        uint defaultValue) =>
        entries.ContainsKey(tag) ? GetScalar(entries, tag) : defaultValue;

    private static uint[] GetLongValues(
        Stream source,
        IReadOnlyDictionary<ushort, TiffEntry> entries,
        ushort tag)
    {
        if (!entries.TryGetValue(tag, out var entry) || entry.Type != TypeLong || entry.Count == 0 || entry.Count > MaxPixelCount)
            throw new InvalidDataException($"TIFF tag {tag} LONG dizisi geçersiz.");
        if (entry.Count == 1) return [entry.ValueOrOffset];
        var byteCount = checked(entry.Count * sizeof(uint));
        EnsureRange(source, entry.ValueOrOffset, byteCount);
        source.Position = entry.ValueOrOffset;
        var values = new uint[checked((int)entry.Count)];
        for (var i = 0; i < values.Length; i++) values[i] = ReadUInt32(source);
        return values;
    }

    private static byte[] GetAsciiBytes(
        Stream source,
        IReadOnlyDictionary<ushort, TiffEntry> entries,
        ushort tag,
        bool required)
    {
        if (!entries.TryGetValue(tag, out var entry))
        {
            if (required) throw new InvalidDataException($"TIFF zorunlu ASCII tag {tag} eksik.");
            return [];
        }
        if (entry.Type != TypeAscii || entry.Count is 0 or > MaxMetadataBytes)
            throw new InvalidDataException($"TIFF ASCII tag {tag} geçersiz.");
        if (entry.Count <= 4)
            throw new InvalidDataException($"TIFF ASCII tag {tag} beklenmeyen inline veri taşıyor.");
        EnsureRange(source, entry.ValueOrOffset, entry.Count);
        source.Position = entry.ValueOrOffset;
        var bytes = new byte[checked((int)entry.Count)];
        ReadExact(source, bytes);
        var terminator = Array.IndexOf(bytes, (byte)0);
        if (terminator < 0)
            throw new InvalidDataException($"TIFF ASCII tag {tag} null terminator taşımıyor.");
        return bytes[..terminator];
    }

    private static void WriteLongEntry(BinaryWriter writer, ushort tag, uint value)
    {
        writer.Write(tag);
        writer.Write(TypeLong);
        writer.Write(1u);
        writer.Write(value);
    }

    private static void WriteShortEntry(BinaryWriter writer, ushort tag, ushort value)
    {
        writer.Write(tag);
        writer.Write(TypeShort);
        writer.Write(1u);
        writer.Write(value);
        writer.Write((ushort)0);
    }

    private static void WriteOffsetEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint offset)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(offset);
    }

    private static void WriteOffsetOrInlineLongArrayEntry(
        BinaryWriter writer,
        ushort tag,
        uint count,
        uint arrayOffset,
        uint singleValue)
    {
        writer.Write(tag);
        writer.Write(TypeLong);
        writer.Write(count);
        writer.Write(count == 1 ? singleValue : arrayOffset);
    }

    private static uint GetStripPixelByteOffset(int width, uint strip) =>
        checked(strip * RowsPerStrip * (uint)width * sizeof(ushort));

    private static uint GetStripByteCount(int width, int height, uint strip)
    {
        var firstRow = checked((long)strip * RowsPerStrip);
        var rows = Math.Min((long)RowsPerStrip, height - firstRow);
        return checked((uint)(rows * width * sizeof(ushort)));
    }

    private static byte[] NullTerminate(byte[] bytes)
    {
        var terminated = new byte[checked(bytes.Length + 1)];
        bytes.CopyTo(terminated, 0);
        return terminated;
    }

    private static uint Align4(uint value) => checked((value + 3u) & ~3u);

    private static void WriteAtOffset(BinaryWriter writer, uint offset, byte[] bytes)
    {
        PadTo(writer, offset);
        writer.Write(bytes);
    }

    private static void PadTo(BinaryWriter writer, uint offset)
    {
        if (writer.BaseStream.Position > offset)
            throw new InvalidDataException("TIFF iç yerleşim ofseti geriye gidemez.");
        while (writer.BaseStream.Position < offset) writer.Write((byte)0);
    }

    private static ushort ReadUInt16(Stream source)
    {
        Span<byte> bytes = stackalloc byte[2];
        ReadExact(source, bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private static uint ReadUInt32(Stream source)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExact(source, bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static void ReadExact(Stream source, Span<byte> destination)
    {
        var readTotal = 0;
        while (readTotal < destination.Length)
        {
            var read = source.Read(destination[readTotal..]);
            if (read == 0) throw new EndOfStreamException("TIFF dosyası yarım veya bozuk.");
            readTotal += read;
        }
    }

    private static void EnsureRange(Stream source, uint offset, uint count)
    {
        var end = checked((ulong)offset + count);
        if (end > (ulong)source.Length)
            throw new EndOfStreamException("TIFF dosyasındaki ofset dosya sınırını aşıyor.");
    }

    private static void SwapBytePairs(Span<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i += 2)
            (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
    }

    private sealed record Gray16TiffMetadata(
        int SchemaVersion,
        Guid AcquisitionId,
        DateTimeOffset Timestamp,
        string Kind,
        string Operation,
        string? SnapFrameMode,
        double TubeCurrent,
        double Voltage,
        double ExposureTime,
        string Profile,
        string SourceMarker,
        int? SourceStrideBytes,
        string? SourceEndian,
        string? SourceOrientation,
        string PixelSha256);

    private readonly record struct TiffEntry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset);
}
