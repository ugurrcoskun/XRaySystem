using Microsoft.Maui.Storage;
using XRaySystem.RemoteClient;

namespace XRaySystem.Mobile.Services;

public sealed class MobileSensorModeSettings
{
    private const string ModeKey = "sensor.runtime.mode";
    private const string UrlKey = "sensor.runtime.url";
    private const string DeviceIdKey = "sensor.runtime.deviceId";

    public SensorRuntimeOptions Load(Uri defaultRemoteAddress)
    {
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(SensorRuntimeOptions.ModeEnvironmentVariable)))
            return SensorRuntimeOptions.FromEnvironment(defaultRemoteAddress);

        try
        {
            var modeText = Preferences.Default.Get(ModeKey, SensorRuntimeMode.Local.ToString());
            var mode = Enum.TryParse<SensorRuntimeMode>(modeText, ignoreCase: true, out var parsed)
                ? parsed
                : SensorRuntimeMode.Local;
            var url = new Uri(
                Preferences.Default.Get(UrlKey, defaultRemoteAddress.AbsoluteUri),
                UriKind.Absolute);
            var deviceId = Preferences.Default.Get(DeviceIdKey, "mock-01");
            var clientOptions = new RemoteDeviceClientOptions(url, deviceId);
            clientOptions.EnsureValid();
            return new SensorRuntimeOptions(mode, url, deviceId);
        }
        catch
        {
            return new SensorRuntimeOptions(
                SensorRuntimeMode.Local,
                defaultRemoteAddress,
                "mock-01");
        }
    }

    public void Save(SensorRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        new RemoteDeviceClientOptions(options.DeviceHostBaseAddress, options.DeviceId).EnsureValid();
        Preferences.Default.Set(ModeKey, options.Mode.ToString());
        Preferences.Default.Set(UrlKey, options.DeviceHostBaseAddress.AbsoluteUri);
        Preferences.Default.Set(DeviceIdKey, options.DeviceId);
    }
}
