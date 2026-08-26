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
var correlatedGrabFrameReceived = new TaskCompletionSource<(RemoteLowFrame Low, GrabFrameReadyEventContract Ready)>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var lowFramesBySequence = new System.Collections.Concurrent.ConcurrentDictionary<(Guid, long), RemoteLowFrame>();
var readyFramesBySequence = new System.Collections.Concurrent.ConcurrentDictionary<(Guid, long), GrabFrameReadyEventContract>();
var grabMeasurementReceived = new TaskCompletionSource<XRayMeasurement>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var snapCompleted = new TaskCompletionSource<SnapProgressEventContract>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var verifiedExposureActive = new TaskCompletionSource<HardwareStateEventContract>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var exposureEnded = new TaskCompletionSource<ExposureEndedEventContract>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var grabFrozen = new TaskCompletionSource<GrabFrozenEventContract>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var remoteErrors = new System.Collections.Concurrent.ConcurrentQueue<DeviceErrorEventContract>();
client.LowFrameReceived += frame =>
{
    lowFrameReceived.TrySetResult(frame);
    var key = (frame.AcquisitionId, frame.FrameSequence);
    lowFramesBySequence[key] = frame;
    if (readyFramesBySequence.TryGetValue(key, out var ready))
        correlatedGrabFrameReceived.TrySetResult((frame, ready));
};
sensor.MeasurementReceived += measurement =>
{
    if (measurement.Frame?.Kind == FrameKind.GrabPreview)
        grabMeasurementReceived.TrySetResult(measurement);
};
client.SnapProgressReceived += progress =>
{
    if (progress.Percent == 100) snapCompleted.TrySetResult(progress);
};
client.HardwareStateReceived += state =>
{
    if (state.XRayActive == true) verifiedExposureActive.TrySetResult(state);
};
client.ExposureEndedReceived += ended => exposureEnded.TrySetResult(ended);
client.GrabFrameReadyReceived += frame =>
{
    var key = (frame.AcquisitionId, frame.SequenceNumber);
    readyFramesBySequence[key] = frame;
    if (lowFramesBySequence.TryGetValue(key, out var low))
        correlatedGrabFrameReceived.TrySetResult((low, frame));
};
client.GrabFrozenReceived += frozen => grabFrozen.TrySetResult(frozen);
client.ErrorReceived += remoteErrors.Enqueue;

try
{
    await sensor.ConnectAsync();
    sensor.Parameters = new AcquisitionParameters(70, 210, 1);
    sensor.Profile = AcquisitionProfile.MedicalSpecimenLikePhantom;
    await sensor.StartCaptureAsync();

    var lowFrame = await lowFrameReceived.Task.WaitAsync(timeout.Token);
    var grabMeasurement = await grabMeasurementReceived.Task.WaitAsync(timeout.Token);
    var activeState = await verifiedExposureActive.Task.WaitAsync(timeout.Token);
    var correlatedFrame = await correlatedGrabFrameReceived.Task.WaitAsync(timeout.Token);
    lowFrame = correlatedFrame.Low;
    var readyEvent = correlatedFrame.Ready;
    if (lowFrame.BmpBytes.Length < 2 ||
        lowFrame.BmpBytes[0] != (byte)'B' ||
        lowFrame.BmpBytes[1] != (byte)'M')
        throw new InvalidDataException("Remote GRAB frame BMP değil.");
    if (grabMeasurement.Frame?.Width != lowFrame.Width ||
        grabMeasurement.Frame.Height != lowFrame.Height)
        throw new InvalidDataException("Remote adapter GRAB frame boyutları uyuşmuyor.");
    if (activeState.ActiveAcquisitionId is null ||
        readyEvent.AcquisitionId != lowFrame.AcquisitionId ||
        readyEvent.SequenceNumber != lowFrame.FrameSequence)
        throw new InvalidDataException("Remote doğrulanmış hardware/GRAB ready eventleri uyuşmuyor.");

    await sensor.StopCaptureAsync();
    await exposureEnded.Task.WaitAsync(timeout.Token);
    await grabFrozen.Task.WaitAsync(timeout.Token);
    var lowSnap = await sensor.SnapAsync(
        new SnapRequest(1, SnapFrameMode.Low),
        timeout.Token);
    var lowMetadata = (await client.GetImagesAsync(timeout.Token))
        .SingleOrDefault(image => image.ImageId == lowSnap.Id)
        ?? throw new InvalidDataException("Remote SNAP LOW metadata bulunamadı.");
    if (!lowMetadata.PersistedOnDevice)
        throw new InvalidDataException("Remote SNAP LOW cihaz bilgisayarında kalıcı doğrulanmadı.");
    var completed = await snapCompleted.Task.WaitAsync(timeout.Token);
    if (completed.DeviceId != deviceId || completed.Percent != 100)
        throw new InvalidDataException("SNAP progress tamamlanma olayı geçersiz.");
    if (remoteErrors.TryPeek(out var remoteError))
        throw new InvalidOperationException($"Remote hata olayı alındı: {remoteError.Code}");
    var lowPreview = await client.GetLowImageAsync(lowSnap.Id, timeout.Token);
    var lowRawBytes = await client.GetRawImageAsync(lowSnap.Id, timeout.Token);
    using var lowRawStream = new MemoryStream(lowRawBytes, writable: false);
    var lowRaw = Gray16RawFileCodec.Read(lowRawStream);
    if (lowRaw.AcquisitionId != lowSnap.Frame!.AcquisitionId ||
        lowRaw.SnapFrameMode != SnapFrameMode.Low ||
        lowSnap.Profile != AcquisitionProfile.MedicalSpecimenLikePhantom)
        throw new InvalidDataException("Remote SNAP LOW kaynak ve metadata uyuşmuyor.");
    if (lowSnap.RequestedParameters.VoltageKv != 70 ||
        lowSnap.RequestedParameters.PowerW != 210 ||
        lowSnap.RequestedParameters.ExposureTimeMs != 1 ||
        lowSnap.Readings.VoltageProvenance != MeasurementProvenance.AppliedTarget ||
        lowSnap.Readings.TubeCurrentProvenance != MeasurementProvenance.Derived)
        throw new InvalidDataException("Remote target/measured provenance sözleşmesi uyuşmuyor.");

    var rawSnap = await sensor.SnapAsync(
        new SnapRequest(1, SnapFrameMode.Raw),
        timeout.Token);
    var rawMetadata = (await client.GetImagesAsync(timeout.Token))
        .SingleOrDefault(image => image.ImageId == rawSnap.Id)
        ?? throw new InvalidDataException("Remote SNAP RAW metadata bulunamadı.");
    if (!rawMetadata.PersistedOnDevice)
        throw new InvalidDataException("Remote SNAP RAW cihaz bilgisayarında kalıcı doğrulanmadı.");
    var rawBytes = await client.GetRawImageAsync(rawSnap.Id, timeout.Token);
    using var rawStream = new MemoryStream(rawBytes, writable: false);
    var raw = Gray16RawFileCodec.Read(rawStream);
    if (raw.AcquisitionId != rawSnap.Frame!.AcquisitionId ||
        raw.SnapFrameMode != SnapFrameMode.Raw ||
        raw.Width != rawSnap.Frame.Width ||
        raw.Height != rawSnap.Frame.Height)
        throw new InvalidDataException("Remote SNAP RAW kaynak ve metadata uyuşmuyor.");

    Console.WriteLine(
        $"PASS device={deviceId} grab={lowFrame.Width}x{lowFrame.Height}" +
        $" snapLow={lowRaw.Width}x{lowRaw.Height} lowPreviewBytes={lowPreview.Length}" +
        $" snapRaw={raw.Width}x{raw.Height} rawBytes={rawBytes.Length}");
}
finally
{
    try { await sensor.StopCaptureAsync(); } catch { }
    try { await sensor.DisconnectAsync(); } catch { }
}
