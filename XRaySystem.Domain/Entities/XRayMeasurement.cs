using System;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Domain.Entities;

public class XRayMeasurement
{
    public Guid Id { get; private set; }
    public DateTime Timestamp { get; private set; }
    public SensorReadings Readings { get; private set; }
    
    // Diske kaydedilen dosyanın tam yolu
    public string? ImagePath { get; private set; }

    // Sensörden gelen ham görüntü verisi (Veritabanına kaydedilmez – DbContext'te Ignore() ile hariç tutulur)
    public ushort[]? ImageMatrix => Frame?.Pixels; // compatibility-only alias; use Frame.Pixels
    public RawGray16Frame? Frame { get; private set; }
    public AcquisitionProfile Profile { get; private set; }
    public bool IsSimulator { get; private set; }

    // EF Core için parametresiz kurucu
    protected XRayMeasurement()
    {
        Readings = null!; // EF Core materialization assigns the owned value object.
        Profile = AcquisitionProfile.IndustrialQaPhantom;
    }

    public XRayMeasurement(SensorReadings readings, RawGray16Frame frame, bool isSimulator = false)
    {
        Id = Guid.NewGuid();
        Timestamp = DateTime.UtcNow;
        Readings = readings;
        Frame = frame;
        Profile = frame.Settings.Profile;
        IsSimulator = isSimulator;
    }

    public void SetImagePath(string path)
    {
        ImagePath = path;
    }
}
