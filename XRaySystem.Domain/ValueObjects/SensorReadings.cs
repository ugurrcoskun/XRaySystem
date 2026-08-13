namespace XRaySystem.Domain.ValueObjects;

public record SensorReadings(
    double TubeCurrent, 
    double Voltage, 
    double XRayIntensity,
    double ExposureTime
);
