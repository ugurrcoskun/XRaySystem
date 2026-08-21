using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Imaging;

namespace XRaySystem.Tests;

public sealed class Gray8BmpDecoderTests
{
    [Fact]
    public void Encoder_and_decoder_preserve_preview_dimensions_and_orientation()
    {
        var frame = CreateFrame();
        var encoded = Gray8BmpEncoder.EncodePreview(frame, 2, 2, 32767.5, 65535);

        var decoded = Gray8BmpDecoder.Decode(encoded.Bytes);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.True(decoded.Pixels[0] < decoded.Pixels[1]);
        Assert.True(decoded.Pixels[2] < decoded.Pixels[3]);
        Assert.Equal(decoded.Pixels.Select(value => (ushort)(value * 257)),
            Gray8BmpDecoder.ExpandToGray16(decoded.Pixels));
    }

    [Fact]
    public void Decoder_rejects_invalid_or_truncated_bmp()
    {
        Assert.Throws<InvalidDataException>(() => Gray8BmpDecoder.Decode([1, 2, 3]));

        var encoded = Gray8BmpEncoder.Encode(CreateFrame(), 32767.5, 65535);
        encoded[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => Gray8BmpDecoder.Decode(encoded));
    }

    private static RawGray16Frame CreateFrame() => new(
        [0, 16384, 32768, ushort.MaxValue],
        2,
        2,
        4,
        Gray16PixelFormat.Gray16LittleEndian,
        FrameKind.LivePreview,
        DateTimeOffset.UtcNow,
        Guid.NewGuid(),
        new AcquisitionSettings(50, 70, 10, AcquisitionProfile.IndustrialQaPhantom),
        "decoder-test");
}
