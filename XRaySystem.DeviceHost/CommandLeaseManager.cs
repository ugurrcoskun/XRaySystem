namespace XRaySystem.DeviceHost;

public sealed class CommandLeaseManager
{
    private readonly object _sync = new();
    private readonly TimeSpan _duration;
    private string? _owner;
    private DateTimeOffset _expiresAt;

    public CommandLeaseManager(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        _duration = duration;
    }

    public bool TryAcquire(string clientId, DateTimeOffset now)
    {
        ValidateClientId(clientId);
        lock (_sync)
        {
            if (_owner is not null && _expiresAt > now &&
                !string.Equals(_owner, clientId, StringComparison.Ordinal))
                return false;

            _owner = clientId;
            _expiresAt = now.Add(_duration);
            return true;
        }
    }

    public void Release(string clientId)
    {
        ValidateClientId(clientId);
        lock (_sync)
        {
            if (!string.Equals(_owner, clientId, StringComparison.Ordinal)) return;
            _owner = null;
            _expiresAt = default;
        }
    }

    public static void ValidateClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("Client ID boş olamaz.", nameof(clientId));
        if (clientId.Length > 128 || clientId.Any(char.IsControl))
            throw new ArgumentException("Client ID en fazla 128 görünür karakter olabilir.", nameof(clientId));
    }
}
