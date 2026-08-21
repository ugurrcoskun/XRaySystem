namespace XRaySystem.Contracts;

public sealed record AcquisitionParametersContract(
    double VoltageKv,
    double PowerW,
    double ExposureTimeMs);

public sealed record MeasuredTelemetryContract(
    double VoltageKv,
    double TubeCurrentMa,
    double PowerW,
    double XRayIntensity,
    double ExposureTimeMs);

public sealed record DeviceStateContract(
    string DeviceId,
    string Status,
    bool IsSimulator,
    AcquisitionParametersContract Parameters,
    long SequenceNumber,
    DateTimeOffset TimestampUtc,
    string Profile = "IndustrialQaPhantom");

public sealed record DeviceCommandRequest(Guid CommandId);

public sealed record UpdateAcquisitionParametersRequest(
    Guid CommandId,
    double VoltageKv,
    double PowerW,
    double ExposureTimeMs,
    string Profile = "IndustrialQaPhantom");

public sealed record DeviceCommandResult(
    Guid CommandId,
    bool Succeeded,
    string Message,
    DeviceStateContract Device);

public sealed record RemoteImageMetadataContract(
    Guid ImageId,
    Guid AcquisitionId,
    string Kind,
    int Width,
    int Height,
    int StrideBytes,
    string PixelFormat,
    int LowWidth,
    int LowHeight,
    DateTimeOffset CapturedAtUtc,
    string Profile,
    AcquisitionParametersContract RequestedParameters,
    MeasuredTelemetryContract MeasuredTelemetry,
    bool LowAvailable,
    bool RawAvailable);

public sealed record SnapCommandResult(
    Guid CommandId,
    bool Succeeded,
    string Message,
    DeviceStateContract Device,
    RemoteImageMetadataContract Image);

public sealed record DeviceStatusEventContract(DeviceStateContract Device);

public sealed record SnapProgressEventContract(
    string DeviceId,
    Guid CommandId,
    string Stage,
    int Percent,
    DateTimeOffset TimestampUtc);

public sealed record DeviceErrorEventContract(
    string DeviceId,
    Guid? CommandId,
    string Operation,
    string Code,
    string Message,
    DateTimeOffset TimestampUtc);

public sealed record TelemetryEventContract(
    string DeviceId,
    long SequenceNumber,
    string DeviceStatus,
    Guid AcquisitionId,
    string FrameKind,
    int Width,
    int Height,
    bool LowFrameAvailable,
    int LowWidth,
    int LowHeight,
    DateTimeOffset CapturedAtUtc,
    string Profile,
    AcquisitionParametersContract RequestedParameters,
    MeasuredTelemetryContract MeasuredTelemetry);

public static class DeviceEventNames
{
    public const string StatusChanged = "device.status.changed";
    public const string TelemetryReceived = "device.telemetry.received";
    public const string SnapProgress = "device.snap.progress";
    public const string ErrorOccurred = "device.error.occurred";
}

public static class DeviceProtocolHeaders
{
    public const string ClientId = "X-XRay-Client-Id";
    public const string ApiKey = "X-XRay-Api-Key";
}

public static class DeviceApiRoutes
{
    public const string Devices = "/api/devices";
    public const string Device = Devices + "/{deviceId}";
    public const string Connect = Device + "/connect";
    public const string Disconnect = Device + "/disconnect";
    public const string Parameters = Device + "/parameters";
    public const string Snap = Device + "/snap";
    public const string Images = Device + "/images";
    public const string Image = "/api/images/{imageId:guid}";
    public const string StartLive = Device + "/live/start";
    public const string StopLive = Device + "/live/stop";
    public const string LatestLiveFrame = Device + "/live/latest";
    public const string EventsHub = "/hubs/device-events";
}
