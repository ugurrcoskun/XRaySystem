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
    public ushort[]? ImageMatrix { get; private set; } 

    // EF Core için parametresiz kurucu
    protected XRayMeasurement() { }

    public XRayMeasurement(SensorReadings readings, ushort[] imageMatrix)
    {
        Id = Guid.NewGuid();
        Timestamp = DateTime.UtcNow;
        Readings = readings;
        ImageMatrix = imageMatrix;
    }

    public void SetImagePath(string path)
    {
        ImagePath = path;
    }
}
