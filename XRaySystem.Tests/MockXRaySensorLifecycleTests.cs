using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Sensors;

namespace XRaySystem.Tests;

public sealed class MockXRaySensorLifecycleTests
{
    [Fact]
    public async Task Snap_low_and_raw_are_distinct_detector_modes_in_the_mock_adapter()
    {
        using var sensor = new MockXRaySensorService();
        await sensor.ConnectAsync();

        var low = await sensor.SnapAsync(new SnapRequest(1, SnapFrameMode.Low));
        var raw = await sensor.SnapAsync(new SnapRequest(1, SnapFrameMode.Raw));

        Assert.Equal((512, 512), (low.Frame!.Width, low.Frame.Height));
        Assert.Equal((2304, 2930), (raw.Frame!.Width, raw.Frame.Height));
        Assert.Equal(SnapFrameMode.Low, low.Frame.SnapFrameMode);
        Assert.Equal(SnapFrameMode.Raw, raw.Frame.SnapFrameMode);
        Assert.Equal(AcquisitionOperation.Snap, low.Frame.Operation);
        Assert.Equal(AcquisitionOperation.Snap, raw.Frame.Operation);
    }

    [Fact(Timeout = 10_000)]
    public async Task Connect_capture_stop_disconnect_follows_expected_states()
    {
        using var sensor = new MockXRaySensorService();
        var measurementReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sensor.MeasurementReceived += _ => measurementReceived.TrySetResult();

        await sensor.ConnectAsync();
        Assert.Equal(DeviceStatus.Connected, sensor.CurrentStatus);

        await sensor.StartCaptureAsync();
        Assert.Equal(DeviceStatus.Capturing, sensor.CurrentStatus);
        await measurementReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await sensor.StopCaptureAsync();
        Assert.Equal(DeviceStatus.Connected, sensor.CurrentStatus);

        await sensor.DisconnectAsync();
        Assert.Equal(DeviceStatus.Disconnected, sensor.CurrentStatus);
    }

    [Fact(Timeout = 10_000)]
    public async Task Snap_during_live_freezes_full_frame_and_resume_restarts_live()
    {
        using var sensor = new MockXRaySensorService();

        await sensor.ConnectAsync();
        await sensor.StartCaptureAsync();
        Assert.Equal(DeviceStatus.Capturing, sensor.CurrentStatus);

        var snap = await sensor.SnapAsync(exposureTime: 1);

        Assert.Equal(DeviceStatus.Frozen, sensor.CurrentStatus);
        Assert.NotNull(snap.Frame);
        Assert.Equal(FrameKind.Snap, snap.Frame.Kind);
        Assert.Equal(2304, snap.Frame.Width);
        Assert.Equal(2930, snap.Frame.Height);

        await sensor.ResumeLiveAsync();
        Assert.Equal(DeviceStatus.Capturing, sensor.CurrentStatus);

        await sensor.StopCaptureAsync();
        await sensor.DisconnectAsync();
    }
}
