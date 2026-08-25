namespace XRaySystem.Domain.ValueObjects;

public enum MeasurementProvenance
{
    Unknown = 0,
    Measured = 1,
    AppliedTarget = 2,
    Derived = 3
}

public record SensorReadings(
    double TubeCurrent,
    double Voltage,
    double XRayIntensity,
    double ExposureTime,
    MeasurementProvenance TubeCurrentProvenance = MeasurementProvenance.Unknown,
    MeasurementProvenance VoltageProvenance = MeasurementProvenance.Unknown,
    MeasurementProvenance ExposureTimeProvenance = MeasurementProvenance.Unknown
);
