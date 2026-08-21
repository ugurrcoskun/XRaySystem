using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Sensors;

namespace XRaySystem.Tests;

public sealed class MockXRaySensorLifecycleTests
{
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
