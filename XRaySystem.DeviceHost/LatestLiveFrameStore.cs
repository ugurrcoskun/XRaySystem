using XRaySystem.Contracts;

namespace XRaySystem.DeviceHost;

public sealed record LatestLiveFrame(TelemetryEventContract Telemetry, byte[] LowBmp);

/// <summary>Keeps exactly one low live frame; newer frames replace older frames atomically.</summary>
public sealed class LatestLiveFrameStore
{
    private LatestLiveFrame? _latest;

    public void Set(TelemetryEventContract telemetry, byte[] lowBmp)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(lowBmp);
        if (!telemetry.LowFrameAvailable)
            throw new ArgumentException("Telemetry low frame bilgisi taşımıyor.", nameof(telemetry));
        if (lowBmp.Length == 0) throw new ArgumentException("Low live frame boş olamaz.", nameof(lowBmp));
        Volatile.Write(ref _latest, new LatestLiveFrame(telemetry, lowBmp.ToArray()));
    }

    public bool TryGetNewerThan(long afterSequence, out LatestLiveFrame? frame)
    {
        var current = Volatile.Read(ref _latest);
        if (current is null || current.Telemetry.SequenceNumber <= afterSequence)
        {
            frame = null;
            return false;
        }

        frame = current;
        return true;
    }

    public void Clear() => Volatile.Write(ref _latest, null);
}
