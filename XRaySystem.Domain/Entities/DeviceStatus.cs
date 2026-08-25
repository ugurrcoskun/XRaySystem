namespace XRaySystem.Domain.Entities;

public enum DeviceStatus
{
    Disconnected,
    Connected,
    Grabbing,
    Capturing = Grabbing,
    Snapping,
    Frozen,
    Error
}
