using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Sensors;
using XRaySystem.Domain.WindowLevel;

static async Task WaitForStatusAsync(MockXRaySensorService sensor, DeviceStatus status)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (sensor.CurrentStatus != status)
    {
        await Task.Delay(10, timeout.Token);
    }
}

static async Task ExpectStoppedSnapAsync(Task<XRayMeasurement> snap)
{
    try
    {
        await snap;
        throw new Exception("Queued Snap unexpectedly started.");
    }
    catch (OperationCanceledException)
    {
    }
    catch (InvalidOperationException)
    {
    }
}

static async Task CancellationBeforeSnapLockAsync()
{
    using var sensor = new MockXRaySensorService();
    await sensor.ConnectAsync();
    var first = sensor.SnapAsync(100);
    await WaitForStatusAsync(sensor, DeviceStatus.Snapping);
    using var cancellation = new CancellationTokenSource();
    var queued = sensor.SnapAsync(100, cancellation.Token);
    cancellation.Cancel();
    await ExpectStoppedSnapAsync(queued);
    await first;
    await sensor.SnapAsync(1); // proves the canceled waiter did not release twice.
}

static async Task QueuedSnapStopAsync()
{
    using var sensor = new MockXRaySensorService();
    await sensor.ConnectAsync();
    var first = sensor.SnapAsync(3000);
    await WaitForStatusAsync(sensor, DeviceStatus.Snapping);
    var queued = sensor.SnapAsync(3000);
    var stop = sensor.StopCaptureAsync();
    await ExpectStoppedSnapAsync(queued);
    await ExpectStoppedSnapAsync(first);
    await stop;
    if (sensor.CurrentStatus != DeviceStatus.Connected)
        throw new Exception("Stop did not return the sensor to Connected.");
}

static async Task QueuedSnapDisconnectAsync()
{
    using var sensor = new MockXRaySensorService();
    await sensor.ConnectAsync();
    var first = sensor.SnapAsync(3000);
    await WaitForStatusAsync(sensor, DeviceStatus.Snapping);
    var queued = sensor.SnapAsync(3000);
    var disconnect = sensor.DisconnectAsync();
    await ExpectStoppedSnapAsync(queued);
    await ExpectStoppedSnapAsync(first);
    await disconnect;
    if (sensor.CurrentStatus != DeviceStatus.Disconnected)
        throw new Exception("Disconnect did not return the sensor to Disconnected.");
}

static string DiskSampleDataPath() => Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "XRaySystem.Infrastructure", "SampleData"));

static async Task DiskLifecycleAsync()
{
    using var sensor = new DiskImageSensorService(DiskSampleDataPath());
    var liveFrames = 0;
    sensor.MeasurementReceived += measurement =>
    {
        if (measurement.Frame?.Kind == FrameKind.LivePreview)
            Interlocked.Increment(ref liveFrames);
    };

    await sensor.ConnectAsync();
    await sensor.StartCaptureAsync();
    using var liveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (Volatile.Read(ref liveFrames) == 0)
        await Task.Delay(10, liveTimeout.Token);

    var first = sensor.SnapAsync(3000);
    while (sensor.CurrentStatus != DeviceStatus.Snapping)
        await Task.Delay(10, liveTimeout.Token);
    var queued = sensor.SnapAsync(3000);
    var stop = sensor.StopCaptureAsync();
    await ExpectStoppedSnapAsync(queued);
    await ExpectStoppedSnapAsync(first);
    await stop;
    if (sensor.CurrentStatus != DeviceStatus.Connected)
        throw new Exception("Disk stop did not return the sensor to Connected.");

    var beforeDisconnect = Volatile.Read(ref liveFrames);
    await sensor.DisconnectAsync();
    await Task.Delay(600);
    if (sensor.CurrentStatus != DeviceStatus.Disconnected || Volatile.Read(ref liveFrames) != beforeDisconnect)
        throw new Exception("Disk disconnect allowed a stale live producer callback.");
}

static async Task ConcurrentDisconnectDisposeAsync()
{
    var sensor = new MockXRaySensorService();
    var firstMeasurement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var blockedMeasurement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseProducer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var blockProducer = 0;

    sensor.MeasurementReceived += _ =>
    {
        if (Volatile.Read(ref blockProducer) == 0)
        {
            firstMeasurement.TrySetResult(true);
            return;
        }

        blockedMeasurement.TrySetResult(true);
        releaseProducer.Task.GetAwaiter().GetResult();
    };

    await sensor.ConnectAsync();
    await sensor.StartCaptureAsync();
    await firstMeasurement.Task;
    Volatile.Write(ref blockProducer, 1);
    await blockedMeasurement.Task.WaitAsync(TimeSpan.FromSeconds(10));

    var disconnect = sensor.DisconnectAsync();
    var disposeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var dispose = Task.Run(() =>
    {
        disposeStarted.SetResult(true);
        sensor.Dispose();
    });
    await disposeStarted.Task;

    if (disconnect.IsCompleted || dispose.IsCompleted)
        throw new Exception("Disconnect/Dispose completed before the producer was released.");

    Console.WriteLine("concurrent teardown: capture callback blocked -> DisconnectAsync admitted -> Dispose requested -> producer released");
    releaseProducer.SetResult(true);
    await disconnect;
    await dispose;

    if (sensor.CurrentStatus != DeviceStatus.Disconnected)
        throw new Exception("Concurrent teardown did not reach Disconnected.");

    Console.WriteLine("concurrent teardown result: DisconnectAsync and Dispose completed; primitives disposed after cleanup");
}

static void WindowLevelMapperChecks()
{
    var source = new ushort[] { 0, 1000, 32768, 65535 };
    var copy = (ushort[])source.Clone();
    var lut = WindowLevelMapper.BuildLut(32767.5, 65535);
    if (lut.Length != 65536 || lut[0] != 0 || lut[65535] != 255) throw new Exception("LUT bounds failed.");
    for (var i = 1; i < lut.Length; i++) if (lut[i] < lut[i - 1]) throw new Exception("LUT monotonicity failed.");
    _ = WindowLevelMapper.Map(source, 32767.5, 65535);
    if (!source.SequenceEqual(copy)) throw new Exception("Mapper mutated input.");
    var known = new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
    if (WindowLevelMapper.HistogramPercentile(known, .01) != 0 || WindowLevelMapper.HistogramPercentile(known, .99) != 9)
        throw new Exception("Percentile failed.");
    var uniform = WindowLevelMapper.Auto(new ushort[] { 123, 123, 123 });
    if (uniform.Center != 123 || uniform.Width != 1) throw new Exception("Uniform auto failed.");
    var empty = WindowLevelMapper.Auto(ReadOnlySpan<ushort>.Empty);
    if (empty.Center != 32767.5 || empty.Width != 65535) throw new Exception("Empty auto failed.");
}

static async Task LatestSchedulerChecksAsync()
{
    var completed = new List<int>();
    await using var scheduler = new LatestWindowLevelScheduler<int>(async (value, token) =>
    {
        await Task.Delay(value == 1 ? 100 : 1, token);
        completed.Add(value);
    });
    scheduler.Submit(1);
    await Task.Delay(10);
    scheduler.Submit(2);
    await Task.Delay(150);
    if (completed.Count != 1 || completed[0] != 2) throw new Exception("Latest generation/cancellation failed.");
    scheduler.Invalidate();
    scheduler.Submit(3);
}

await CancellationBeforeSnapLockAsync();
await QueuedSnapStopAsync();
await QueuedSnapDisconnectAsync();
await DiskLifecycleAsync();
await ConcurrentDisconnectDisposeAsync();
WindowLevelMapperChecks();
await LatestSchedulerChecksAsync();
Console.WriteLine("Sensor lifecycle concurrency harness passed.");
