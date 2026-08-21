namespace XRaySystem.RemoteClient;

public enum SensorRuntimeMode
{
    Local,
    Remote
}

public sealed record SensorRuntimeOptions(
    SensorRuntimeMode Mode,
    Uri DeviceHostBaseAddress,
    string DeviceId)
{
    public const string ModeEnvironmentVariable = "XRAYSYSTEM_SENSOR_MODE";
    public const string UrlEnvironmentVariable = "XRAYSYSTEM_DEVICEHOST_URL";
    public const string DeviceIdEnvironmentVariable = "XRAYSYSTEM_DEVICE_ID";

    public static SensorRuntimeOptions FromEnvironment(Uri defaultRemoteAddress)
    {
        ArgumentNullException.ThrowIfNull(defaultRemoteAddress);
        var modeText = Environment.GetEnvironmentVariable(ModeEnvironmentVariable);
        var mode = modeText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "local" or "mock" or "standalone" => SensorRuntimeMode.Local,
            "remote" or "client" => SensorRuntimeMode.Remote,
            _ => throw new InvalidOperationException(
                $"{ModeEnvironmentVariable} değeri Local veya Remote olmalıdır.")
        };

        var urlText = Environment.GetEnvironmentVariable(UrlEnvironmentVariable);
        var baseAddress = string.IsNullOrWhiteSpace(urlText)
            ? defaultRemoteAddress
            : new Uri(urlText, UriKind.Absolute);
        var deviceId = Environment.GetEnvironmentVariable(DeviceIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(deviceId)) deviceId = "mock-01";

        var clientOptions = new RemoteDeviceClientOptions(baseAddress, deviceId);
        clientOptions.EnsureValid();
        return new SensorRuntimeOptions(mode, baseAddress, deviceId);
    }
}
