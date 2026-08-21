using XRaySystem.Contracts;
using XRaySystem.DeviceHost;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Imaging;

namespace XRaySystem.Tests;

public sealed class RemoteImageBufferTests
{
    [Fact]
    public void Count_limit_evicts_the_oldest_remote_image_only()
    {
        var buffer = new RemoteImageBuffer(new RemoteImageBufferOptions(2, 1024, null));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        Add(buffer, first);
        Add(buffer, second);
        Add(buffer, third);

        Assert.Equal(2, buffer.Count);
        Assert.False(buffer.TryGetRaw(first, out _));
        Assert.True(buffer.TryGetRaw(second, out _));
        Assert.True(buffer.TryGetRaw(third, out _));
        Assert.Equal([third, second], buffer.GetMetadata().Select(item => item.ImageId));
    }

    [Fact]
    public void Byte_limit_evicts_oldest_entries()
    {
        // Her test frame'i 8 raw byte + 2 preview byte kullanır.
        var buffer = new RemoteImageBuffer(new RemoteImageBufferOptions(10, 15, null));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Add(buffer, first);
        Add(buffer, second);

        Assert.Equal(1, buffer.Count);
        Assert.Equal(10, buffer.TotalBytes);
        Assert.False(buffer.TryGetLow(first, out _));
        Assert.True(buffer.TryGetLow(second, out _));
    }

    [Fact]
    public void Time_to_live_removes_expired_entries()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero));
        var buffer = new RemoteImageBuffer(
            new RemoteImageBufferOptions(10, 1024, TimeSpan.FromMinutes(5)),
            clock);
        var imageId = Guid.NewGuid();
        Add(buffer, imageId);

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.TryGetRaw(imageId, out _));
    }

    [Fact]
    public void Gray8_bmp_encoder_writes_a_padded_indexed_bitmap()
    {
        var settings = new AcquisitionSettings(1, 70, 10, AcquisitionProfile.IndustrialQaPhantom);
        var frame = new RawGray16Frame(
            [0, 32768, 65535, 65535, 32768, 0],
            3,
            2,
            6,
            Gray16PixelFormat.Gray16LittleEndian,
            FrameKind.Snap,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            settings,
            "test");

        var bmp = Gray8BmpEncoder.Encode(frame, 32767.5, 65535);

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(1086, bmp.Length);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(1078, BitConverter.ToInt32(bmp, 10));
    }

    [Fact]
    public void Preview_encoder_preserves_aspect_ratio_and_limits_dimensions()
    {
        var settings = new AcquisitionSettings(1, 70, 10, AcquisitionProfile.IndustrialQaPhantom);
        var pixels = Enumerable.Range(0, 12).Select(value => (ushort)(value * 1000)).ToArray();
        var frame = new RawGray16Frame(
            pixels,
            4,
            3,
            8,
            Gray16PixelFormat.Gray16LittleEndian,
            FrameKind.Snap,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            settings,
            "test");

        var preview = Gray8BmpEncoder.EncodePreview(frame, 2, 2, 5000, 10000);

        Assert.Equal(2, preview.Width);
        Assert.Equal(1, preview.Height);
        Assert.Equal(1082, preview.Bytes.Length);
    }

    private static void Add(RemoteImageBuffer buffer, Guid imageId)
    {
        var frame = CreateFrame();
        var requested = new AcquisitionParametersContract(70, 70, 10);
        var measured = new MeasuredTelemetryContract(70, 1, 70, 100, 10);
        var metadata = new RemoteImageMetadataContract(
            imageId,
            frame.AcquisitionId,
            nameof(FrameKind.Snap),
            frame.Width,
            frame.Height,
            frame.StrideBytes,
            nameof(Gray16PixelFormat.Gray16LittleEndian),
            frame.Width,
            frame.Height,
            frame.Timestamp,
            nameof(AcquisitionProfile.IndustrialQaPhantom),
            requested,
            measured,
            true,
            true);
        buffer.Add(metadata, frame, [0, 1]);
    }

    private static RawGray16Frame CreateFrame()
    {
        var settings = new AcquisitionSettings(1, 70, 10, AcquisitionProfile.IndustrialQaPhantom);
        return new RawGray16Frame(
            [1, 2, 3, 4],
            2,
            2,
            4,
            Gray16PixelFormat.Gray16LittleEndian,
            FrameKind.Snap,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            settings,
            "test");
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
