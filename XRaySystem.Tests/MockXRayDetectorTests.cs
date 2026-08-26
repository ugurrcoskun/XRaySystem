using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Sensors;

namespace XRaySystem.Tests;

public sealed class MockXRayDetectorTests
{
    [Theory]
    [InlineData(SnapFrameMode.Low, 512, 512)]
    [InlineData(SnapFrameMode.Raw, 2304, 2930)]
    public async Task Snap_returns_owned_gray16_qa_phantom_with_expected_geometry(
        SnapFrameMode mode,
        int expectedWidth,
        int expectedHeight)
    {
        using var detector = new MockXRayDetector();
        await detector.ConnectAsync(CancellationToken.None);
        var acquisitionId = Guid.NewGuid();
        await detector.ConfigureSnapAsync(
            acquisitionId,
            new DetectorSnapSettings(1, mode),
            CancellationToken.None);
        await detector.ArmAsync(acquisitionId, CancellationToken.None);
        await detector.StartSnapAsync(acquisitionId, CancellationToken.None);

        var token = await detector.WaitForFrameAsync(CancellationToken.None);
        var firstCopy = await detector.CopyFrameAsync(token, CancellationToken.None);
        var secondCopy = await detector.CopyFrameAsync(token, CancellationToken.None);

        Assert.Equal(expectedWidth, token.Width);
        Assert.Equal(expectedHeight, token.Height);
        Assert.Equal(expectedWidth * sizeof(ushort), token.StrideBytes);
        Assert.Equal(Gray16PixelFormat.Gray16LittleEndian, token.PixelFormat);
        Assert.Equal(checked(expectedWidth * expectedHeight), firstCopy.Length);
        Assert.NotSame(firstCopy, secondCopy);
        Assert.Equal(firstCopy, secondCopy);
        Assert.True(firstCopy.Distinct().Take(1_025).Count() > 1_024);
        Assert.True(firstCopy.Max() - firstCopy.Min() > 8_000);

        var originalFirstPixel = secondCopy[0];
        firstCopy[0] = ushort.MaxValue;
        Assert.Equal(originalFirstPixel, secondCopy[0]);

        await detector.ReleaseFrameAsync(token, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            detector.CopyFrameAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Grab_produces_a_new_dynamic_phantom_for_each_detector_notification()
    {
        using var detector = new MockXRayDetector();
        await detector.ConnectAsync(CancellationToken.None);
        var sessionId = Guid.NewGuid();
        await detector.ConfigureGrabAsync(
            sessionId,
            new DetectorGrabSettings(1),
            CancellationToken.None);
        await detector.ArmAsync(sessionId, CancellationToken.None);
        await detector.StartGrabAsync(sessionId, CancellationToken.None);

        var firstToken = await detector.WaitForFrameAsync(CancellationToken.None);
        var firstPixels = await detector.CopyFrameAsync(firstToken, CancellationToken.None);
        await detector.ReleaseFrameAsync(firstToken, CancellationToken.None);

        var secondToken = await detector.WaitForFrameAsync(CancellationToken.None);
        var secondPixels = await detector.CopyFrameAsync(secondToken, CancellationToken.None);
        await detector.ReleaseFrameAsync(secondToken, CancellationToken.None);

        Assert.True(secondToken.SequenceNumber > firstToken.SequenceNumber);
        Assert.False(firstPixels.SequenceEqual(secondPixels));

        await detector.FreezeAsync(sessionId, CancellationToken.None);
        await detector.StopAsync(CancellationToken.None);
    }
}
