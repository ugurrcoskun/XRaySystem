using XRaySystem.Domain.Entities;
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
}
