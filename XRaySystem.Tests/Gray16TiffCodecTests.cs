using System.Buffers.Binary;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;

namespace XRaySystem.Tests;

public sealed class Gray16TiffCodecTests
{
    [Fact]
    public void Round_trip_writes_standard_unsigned_gray16_tags_and_preserves_frame()
    {
        var frame = CreateFrame(height: 2);
        using var stream = new MemoryStream();

        Gray16TiffCodec.Write(stream, frame);
        var bytes = stream.ToArray();

        Assert.Equal((byte)'I', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((ushort)42, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)));
        Assert.Equal(16u, ReadScalarTag(bytes, 258));
        Assert.Equal(1u, ReadScalarTag(bytes, 259));
        Assert.Equal(1u, ReadScalarTag(bytes, 262));
        Assert.Equal(1u, ReadScalarTag(bytes, 277));
        Assert.Equal(1u, ReadScalarTag(bytes, 339));

        stream.Position = 0;
        var restored = Gray16TiffCodec.Read(stream);

        Assert.Equal(frame.Pixels, restored.Pixels);
        Assert.Equal(frame.Width, restored.Width);
        Assert.Equal(frame.Height, restored.Height);
        Assert.Equal(frame.StrideBytes, restored.StrideBytes);
        Assert.Equal(frame.PixelFormat, restored.PixelFormat);
        Assert.Equal(frame.Kind, restored.Kind);
        Assert.Equal(frame.Timestamp, restored.Timestamp);
        Assert.Equal(frame.AcquisitionId, restored.AcquisitionId);
        Assert.Equal(frame.Settings, restored.Settings);
        Assert.Equal(frame.SourceMarker, restored.SourceMarker);
        Assert.Equal(frame.SourceStrideBytes, restored.SourceStrideBytes);
        Assert.Equal(frame.SourceEndian, restored.SourceEndian);
        Assert.Equal(frame.SourceOrientation, restored.SourceOrientation);
        Assert.Equal(frame.Operation, restored.Operation);
        Assert.Equal(frame.SnapFrameMode, restored.SnapFrameMode);
    }

    [Fact]
    public void Multi_strip_boundary_round_trip_is_lossless()
    {
        var frame = CreateFrame(height: 129);
        using var stream = new MemoryStream();

        Gray16TiffCodec.Write(stream, frame);
        stream.Position = 0;
        var restored = Gray16TiffCodec.Read(stream);

        Assert.Equal(frame.Pixels, restored.Pixels);
        Assert.Equal(3u, ReadTagCount(stream.ToArray(), 273));
        Assert.Equal(3u, ReadTagCount(stream.ToArray(), 279));
    }

    [Fact]
    public void Corrupted_pixel_payload_fails_integrity_validation()
    {
        using var stream = new MemoryStream();
        Gray16TiffCodec.Write(stream, CreateFrame(height: 2));
        var bytes = stream.ToArray();
        var pixelOffset = checked((int)ReadScalarTag(bytes, 273));
        bytes[pixelOffset] ^= 0x5A;

        using var corrupted = new MemoryStream(bytes, writable: false);
        var error = Assert.Throws<InvalidDataException>(() => Gray16TiffCodec.Read(corrupted));

        Assert.Contains("bütünlük", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_gray16_tiff_is_rejected()
    {
        using var stream = new MemoryStream();
        Gray16TiffCodec.Write(stream, CreateFrame(height: 2));
        var bytes = stream.ToArray();
        WriteInlineShortTag(bytes, 258, 8);

        using var invalid = new MemoryStream(bytes, writable: false);
        Assert.Throws<InvalidDataException>(() => Gray16TiffCodec.Read(invalid));
    }

    [Fact]
    public void Truncated_tiff_is_rejected()
    {
        using var stream = new MemoryStream();
        Gray16TiffCodec.Write(stream, CreateFrame(height: 2));
        var bytes = stream.ToArray()[..^1];

        using var truncated = new MemoryStream(bytes, writable: false);
        Assert.Throws<EndOfStreamException>(() => Gray16TiffCodec.Read(truncated));
    }

    private static RawGray16Frame CreateFrame(int height)
    {
        const int width = 3;
        var pixels = Enumerable.Range(0, checked(width * height))
            .Select(value => checked((ushort)(value * 251 % ushort.MaxValue)))
            .ToArray();
        return new RawGray16Frame(
            pixels,
            width,
            height,
            width * sizeof(ushort),
            Gray16PixelFormat.Gray16LittleEndian,
            FrameKind.Snap,
            new DateTimeOffset(2026, 8, 27, 12, 34, 56, TimeSpan.FromHours(3)),
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            new AcquisitionSettings(3, 70, 500, AcquisitionProfile.IndustrialQaPhantom),
            "unit-test",
            sourceStrideBytes: 8,
            sourceEndian: "little",
            sourceOrientation: "top-left",
            operation: AcquisitionOperation.Snap,
            snapFrameMode: SnapFrameMode.Raw);
    }

    private static uint ReadScalarTag(byte[] bytes, ushort tag)
    {
        var entry = FindTag(bytes, tag);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(2, 2));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
        Assert.Equal(1u, count);
        return type == 3
            ? BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(8, 2))
            : BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
    }

    private static uint ReadTagCount(byte[] bytes, ushort tag) =>
        BinaryPrimitives.ReadUInt32LittleEndian(FindTag(bytes, tag).Slice(4, 4));

    private static void WriteInlineShortTag(byte[] bytes, ushort tag, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(FindTag(bytes, tag).Slice(8, 2), value);

    private static Span<byte> FindTag(byte[] bytes, ushort tag)
    {
        var ifdOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ifdOffset, 2));
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.AsSpan(ifdOffset + 2 + i * 12, 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry[..2]) == tag) return entry;
        }
        throw new Xunit.Sdk.XunitException($"TIFF tag {tag} bulunamadı.");
    }
}
