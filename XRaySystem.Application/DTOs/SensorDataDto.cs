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
    public AcquisitionParameters? RequestedParameters { get; set; }
    public Guid AcquisitionId { get; set; }
    public AcquisitionOperation Operation { get; set; }
    public SnapFrameMode? SnapFrameMode { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public string? SourceMarker { get; set; }
    public MeasurementProvenance VoltageProvenance { get; set; }
    public MeasurementProvenance TubeCurrentProvenance { get; set; }
    public MeasurementProvenance ExposureTimeProvenance { get; set; }
    public string? ImagePath { get; set; }
    public string? RawImagePath { get; set; }
}
