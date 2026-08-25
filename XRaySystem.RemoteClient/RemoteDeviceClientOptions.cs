namespace XRaySystem.RemoteClient;

public sealed record RemoteDeviceClientOptions(
    Uri BaseAddress,
    string DeviceId,
    string? ClientId = null,
    string? ApiKey = null,
    Func<CancellationToken, Task<string?>>? ApiKeyProvider = null)
{
    public void EnsureValid()
    {
        ArgumentNullException.ThrowIfNull(BaseAddress);
        if (!BaseAddress.IsAbsoluteUri ||
            BaseAddress.Scheme is not ("http" or "https"))
            throw new ArgumentException("DeviceHost adresi mutlak HTTP/HTTPS URI olmalıdır.", nameof(BaseAddress));
        if (string.IsNullOrWhiteSpace(DeviceId))
            throw new ArgumentException("DeviceId boş olamaz.", nameof(DeviceId));
        if (DeviceId.Length > 128 || DeviceId.Any(char.IsControl))
            throw new ArgumentException("DeviceId en fazla 128 görünür karakter olabilir.", nameof(DeviceId));
        if (!string.IsNullOrWhiteSpace(ClientId) &&
            (ClientId.Length > 128 || ClientId.Any(char.IsControl)))
            throw new ArgumentException("ClientId en fazla 128 görünür karakter olabilir.", nameof(ClientId));
    }
}
