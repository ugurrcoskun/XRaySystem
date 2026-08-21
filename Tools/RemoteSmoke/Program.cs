using XRaySystem.Contracts;
using XRaySystem.Infrastructure.Storage;
using XRaySystem.RemoteClient;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;

var baseAddress = args.Length > 0
    ? new Uri(args[0], UriKind.Absolute)
    : new Uri("http://127.0.0.1:5088");
var deviceId = args.Length > 1 ? args[1] : "mock-01";
var apiKey = args.Length > 2
    ? args[2]
    : Environment.GetEnvironmentVariable("XRAYSYSTEM_DEVICEHOST_API_KEY");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
using var httpClient = new HttpClient();
await using var client = new RemoteDeviceClient(
    httpClient,
    new RemoteDeviceClientOptions(baseAddress, deviceId, ApiKey: apiKey));
using var sensor = new RemoteSensorService(client);
var lowFrameReceived = new TaskCompletionSource<RemoteLowFrame>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var liveMeasurementReceived = new TaskCompletionSource<XRayMeasurement>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var snapCompleted = new TaskCompletionSource<SnapProgressEventContract>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var remoteErrors = new System.Collections.Concurrent.ConcurrentQueue<DeviceErrorEventContract>();
client.LowFrameReceived += frame => lowFrameReceived.TrySetResult(frame);
sensor.MeasurementReceived += measurement =>
{
    if (measurement.Frame?.Kind == FrameKind.LivePreview)
        liveMeasurementReceived.TrySetResult(measurement);
};
client.SnapProgressReceived += progress =>
{
    if (progress.Percent == 100) snapCompleted.TrySetResult(progress);
};
client.ErrorReceived += remoteErrors.Enqueue;

try
{
    await sensor.ConnectAsync();
    sensor.Parameters = new AcquisitionParameters(70, 3500, 1);
    sensor.Profile = AcquisitionProfile.MedicalSpecimenLikePhantom;
    await sensor.StartCaptureAsync();

    var lowFrame = await lowFrameReceived.Task.WaitAsync(timeout.Token);
    var liveMeasurement = await liveMeasurementReceived.Task.WaitAsync(timeout.Token);
    if (lowFrame.BmpBytes.Length < 2 ||
        lowFrame.BmpBytes[0] != (byte)'B' ||
        lowFrame.BmpBytes[1] != (byte)'M')
        throw new InvalidDataException("Remote live frame BMP değil.");
    if (liveMeasurement.Frame?.Width != lowFrame.Width ||
        liveMeasurement.Frame.Height != lowFrame.Height)
        throw new InvalidDataException("Remote adapter live frame boyutları uyuşmuyor.");

    await sensor.StopCaptureAsync();
    var snap = await sensor.SnapAsync(1, timeout.Token);
    var completed = await snapCompleted.Task.WaitAsync(timeout.Token);
    if (completed.DeviceId != deviceId || completed.Percent != 100)
        throw new InvalidDataException("SNAP progress tamamlanma olayı geçersiz.");
    if (remoteErrors.TryPeek(out var remoteError))
        throw new InvalidOperationException($"Remote hata olayı alındı: {remoteError.Code}");
    var low = await client.GetLowImageAsync(snap.Id, timeout.Token);
    var rawBytes = await client.GetRawImageAsync(snap.Id, timeout.Token);
    using var rawStream = new MemoryStream(rawBytes, writable: false);
    var raw = Gray16RawFileCodec.Read(rawStream);
    if (raw.AcquisitionId != snap.Frame!.AcquisitionId ||
        raw.Width != snap.Frame.Width ||
        raw.Height != snap.Frame.Height ||
        snap.Profile != AcquisitionProfile.MedicalSpecimenLikePhantom)
        throw new InvalidDataException("Remote raw ve SNAP metadata uyuşmuyor.");

    Console.WriteLine(
        $"PASS device={deviceId} live={lowFrame.Width}x{lowFrame.Height}" +
        $" lowBytes={low.Length} raw={raw.Width}x{raw.Height} rawBytes={rawBytes.Length}");
}
finally
{
    try { await sensor.StopCaptureAsync(); } catch { }
    try { await sensor.DisconnectAsync(); } catch { }
}
