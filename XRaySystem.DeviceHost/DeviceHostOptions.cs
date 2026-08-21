namespace XRaySystem.DeviceHost;

public sealed record DeviceHostOptions(string DeviceId, bool IsSimulator)
{
    public const string DefaultMockDeviceId = "mock-01";
}
