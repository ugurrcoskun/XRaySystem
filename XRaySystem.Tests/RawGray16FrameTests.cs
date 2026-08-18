using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Tests;

public sealed class RawGray16FrameTests
{
    [Fact]
    public void Valid_frame_preserves_canonical_metadata()
    {
        var frame = Create(new ushort[6], width: 3, height: 2, stride: 6);

        Assert.Equal(6, frame.PixelCount);
        Assert.Equal("little", frame.SourceEndian);
        Assert.Equal("top-left", frame.SourceOrientation);
    }

    [Fact]
    public void Pixel_count_must_match_dimensions() =>
        Assert.Throws<ArgumentException>(() => Create(new ushort[5], width: 3, height: 2, stride: 6));

    [Fact]
    public void Canonical_stride_must_be_width_times_two() =>
        Assert.Throws<ArgumentException>(() => Create(new ushort[6], width: 3, height: 2, stride: 8));

    private static RawGray16Frame Create(ushort[] pixels, int width, int height, int stride) =>
        new(
            pixels, width, height, stride,
            Gray16PixelFormat.Gray16LittleEndian,
            FrameKind.LivePreview,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            new AcquisitionSettings(5, 80, 33, AcquisitionProfile.IndustrialQaPhantom),
            "unit-test",
            sourceStrideBytes: stride,
            sourceEndian: "little",
            sourceOrientation: "top-left");
}
