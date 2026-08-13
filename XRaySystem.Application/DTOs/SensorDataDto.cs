using System;

namespace XRaySystem.Application.DTOs;

public class SensorDataDto
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public double TubeCurrent { get; set; }
    public double Voltage { get; set; }
    public double XRayIntensity { get; set; }
    public double ExposureTime { get; set; }
    public ushort[] ImageMatrix { get; set; } = Array.Empty<ushort>();
    public string? ImagePath { get; set; }
}
