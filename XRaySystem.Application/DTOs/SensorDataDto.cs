using System;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Application.DTOs;

public class SensorDataDto
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public double PowerW { get; set; }
    public double Voltage { get; set; }
    public double XRayIntensity { get; set; }
    public double ExposureTime { get; set; }
    public RawGray16Frame? Frame { get; set; }
    public AcquisitionProfile Profile { get; set; }
    public bool IsSimulator { get; set; }
    public string? ImagePath { get; set; }
    public string? RawImagePath { get; set; }
}
