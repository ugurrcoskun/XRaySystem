using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;

namespace XRaySystem.Tests;

public sealed class Gray16RawFileCodecTests
{
    [Fact]
    public void Round_trip_preserves_gray16_pixels_and_metadata()
    {
        var frame = CreateFrame();
        using var stream = new MemoryStream();

        Gray16RawFileCodec.Write(stream, frame);
        stream.Position = 0;
        var restored = Gray16RawFileCodec.Read(stream);

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
    }

    [Fact]
    public void Corrupted_pixel_payload_fails_integrity_validation()
    {
        using var stream = new MemoryStream();
        Gray16RawFileCodec.Write(stream, CreateFrame());
        var bytes = stream.ToArray();
        bytes[^33] ^= 0x5A;

        using var corrupted = new MemoryStream(bytes);
        var error = Assert.Throws<InvalidDataException>(() => Gray16RawFileCodec.Read(corrupted));

        Assert.Contains("bütünlük", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncated_file_is_rejected()
    {
        using var stream = new MemoryStream();
        Gray16RawFileCodec.Write(stream, CreateFrame());
        var bytes = stream.ToArray()[..^10];

        using var truncated = new MemoryStream(bytes);
        Assert.Throws<EndOfStreamException>(() => Gray16RawFileCodec.Read(truncated));
    }

    private static RawGray16Frame CreateFrame() => new(
        [0, 1, 255, 1024, 32768, 65535],
        width: 3,
        height: 2,
        strideBytes: 6,
        Gray16PixelFormat.Gray16LittleEndian,
        FrameKind.Snap,
        new DateTimeOffset(2026, 8, 19, 12, 34, 56, TimeSpan.FromHours(3)),
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        new AcquisitionSettings(50, 70, 500, AcquisitionProfile.IndustrialQaPhantom),
        "unit-test",
        sourceStrideBytes: 8,
        sourceEndian: "little",
        sourceOrientation: "top-left");
}
