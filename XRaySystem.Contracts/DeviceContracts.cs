using System.Text.Json.Serialization;

namespace XRaySystem.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapFrameModeContract
{
    Raw = 0,
    Low = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AcquisitionOperationContract
{
    Snap = 0,
    Grab = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeasurementProvenanceContract
{
    Unknown = 0,
    Measured = 1,
    AppliedTarget = 2,
    Derived = 3
}

public sealed record AcquisitionParametersContract(
    double VoltageKv,
    double PowerW,
    double ExposureTimeMs);

public sealed record MeasuredTelemetryContract(
    double VoltageKv,
    double TubeCurrentMa,
    double PowerW,
    double XRayIntensity,
    double ExposureTimeMs,
    MeasurementProvenanceContract VoltageProvenance = MeasurementProvenanceContract.Unknown,
    MeasurementProvenanceContract TubeCurrentProvenance = MeasurementProvenanceContract.Unknown,
    MeasurementProvenanceContract ExposureTimeProvenance = MeasurementProvenanceContract.Unknown);

public sealed record DeviceStateContract(
    string DeviceId,
    string Status,
    bool IsSimulator,
    AcquisitionParametersContract Parameters,
    long SequenceNumber,
    DateTimeOffset TimestampUtc,
    string Profile = "IndustrialQaPhantom",
    string? CoordinatorState = null,
    bool? GeneratorConnected = null,
    bool? DetectorConnected = null,
    bool? XRayActive = null,
    bool? DetectorAcquiring = null,
    Guid? ActiveAcquisitionId = null);

public sealed record DeviceProtocolContract(
    int ProtocolVersion,
    string Service,
    string DeviceId,
    bool IsSimulator,
    IReadOnlyList<string> AcquisitionOperations,
    IReadOnlyList<string> SnapFrameModes);

public static class DeviceProtocolVersion
{
    public const int Current = 2;
}

public sealed record DeviceCommandRequest(Guid CommandId);

public sealed record SnapCommandRequest(
    Guid CommandId,
    SnapFrameModeContract FrameMode = SnapFrameModeContract.Raw);

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
    bool RawAvailable,
    AcquisitionOperationContract Operation = AcquisitionOperationContract.Snap,
    SnapFrameModeContract? SnapFrameMode = SnapFrameModeContract.Raw,
    bool PersistedOnDevice = false);

public sealed record SnapCommandResult(
    Guid CommandId,
    bool Succeeded,
    string Message,
    DeviceStateContract Device,
    RemoteImageMetadataContract Image);

public sealed record DeviceStatusEventContract(DeviceStateContract Device);

public sealed record HardwareStateEventContract(
    string DeviceId,
    long SequenceNumber,
    string? CoordinatorState,
    bool? GeneratorConnected,
    bool? DetectorConnected,
    bool? XRayActive,
    bool? DetectorAcquiring,
    Guid? ActiveAcquisitionId,
    DateTimeOffset TimestampUtc);

public sealed record FrameReadyEventContract(
    string DeviceId,
    long SequenceNumber,
    Guid AcquisitionId,
    AcquisitionOperationContract Operation,
    string FrameKind,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc);

public sealed record GrabFrameReadyEventContract(
    string DeviceId,
    long SequenceNumber,
    Guid AcquisitionId,
    int LowWidth,
    int LowHeight,
    DateTimeOffset CapturedAtUtc);

public sealed record GrabFrozenEventContract(
    string DeviceId,
    long SequenceNumber,
    Guid? LastAcquisitionId,
    DateTimeOffset TimestampUtc);

public sealed record ExposureEndedEventContract(
    string DeviceId,
    long SequenceNumber,
    Guid? AcquisitionId,
    DateTimeOffset TimestampUtc);

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
    MeasuredTelemetryContract MeasuredTelemetry,
    AcquisitionOperationContract Operation = AcquisitionOperationContract.Grab);

public static class DeviceEventNames
{
    public const string StatusChanged = "device.status.changed";
    public const string HardwareStateChanged = "device.hardware.state.changed";
    public const string TelemetryReceived = "device.telemetry.received";
    public const string FrameReady = "device.frame.ready";
    public const string ExposureEnded = "device.exposure.ended";
    public const string SnapProgress = "device.snap.progress";
    public const string GrabFrameReady = "device.grab.frame.ready";
    public const string GrabFrozen = "device.grab.frozen";
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
    public const string Protocol = "/api/protocol";
    public const string Device = Devices + "/{deviceId}";
    public const string Connect = Device + "/connect";
    public const string Disconnect = Device + "/disconnect";
    public const string Parameters = Device + "/parameters";
    public const string Snap = Device + "/snap";
    public const string StartGrab = Device + "/grab/start";
    public const string FreezeGrab = Device + "/grab/freeze";
    public const string LatestGrabFrame = Device + "/grab/latest";
    public const string Abort = Device + "/abort";
    public const string Images = Device + "/images";
    public const string Image = "/api/images/{imageId:guid}";
    // One-release compatibility aliases for clients built before the GRAB migration.
    public const string StartLive = Device + "/live/start";
    public const string StopLive = Device + "/live/stop";
    public const string LatestLiveFrame = Device + "/live/latest";
    public const string EventsHub = "/hubs/device-events";
}
