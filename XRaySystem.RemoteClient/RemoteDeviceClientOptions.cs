namespace XRaySystem.RemoteClient;

public sealed record RemoteDeviceClientOptions(
    Uri BaseAddress,
    string DeviceId,
    string? ClientId = null,
    string? ApiKey = null)
{
    public void EnsureValid()
    {
        ArgumentNullException.ThrowIfNull(BaseAddress);
        if (!BaseAddress.IsAbsoluteUri ||
            BaseAddress.Scheme is not ("http" or "https"))
            throw new ArgumentException("DeviceHost adresi mutlak HTTP/HTTPS URI olmalıdır.", nameof(BaseAddress));
        if (string.IsNullOrWhiteSpace(DeviceId))
            throw new ArgumentException("DeviceId boş olamaz.", nameof(DeviceId));
    }
}
