using XRaySystem.Contracts;
using XRaySystem.DeviceHost;

namespace XRaySystem.Tests;

public sealed class LatestLiveFrameStoreTests
{
    [Fact]
    public void New_frame_atomically_replaces_the_previous_frame()
    {
        var store = new LatestLiveFrameStore();
        store.Set(CreateTelemetry(10), [1, 2]);
        store.Set(CreateTelemetry(11), [3, 4]);

        Assert.True(store.TryGetNewerThan(10, out var latest));
        Assert.NotNull(latest);
        Assert.Equal(11, latest.Telemetry.SequenceNumber);
        Assert.Equal([3, 4], latest.LowBmp);
        Assert.False(store.TryGetNewerThan(11, out _));
    }

    [Fact]
    public void Clear_removes_the_current_live_frame()
    {
        var store = new LatestLiveFrameStore();
        store.Set(CreateTelemetry(1), [1]);

        store.Clear();

        Assert.False(store.TryGetNewerThan(-1, out _));
    }

    private static TelemetryEventContract CreateTelemetry(long sequence) => new(
        "mock-01",
        sequence,
        "Capturing",
        Guid.NewGuid(),
        "LivePreview",
        512,
        512,
        LowFrameAvailable: true,
        LowWidth: 512,
        LowHeight: 512,
        DateTimeOffset.UtcNow,
        "IndustrialQaPhantom",
        new AcquisitionParametersContract(70, 3500, 10),
        new MeasuredTelemetryContract(70, 50, 3500, 1000, 10));
}
