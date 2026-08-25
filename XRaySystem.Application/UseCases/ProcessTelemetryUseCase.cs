using System;
using XRaySystem.Application.DTOs;
using XRaySystem.Domain.Entities;

namespace XRaySystem.Application.UseCases;

public class ProcessTelemetryUseCase
{
    // Bu UseCase, Domain katmanından gelen saf (raw) Entity verisini, 
    // UI (WPF) katmanında gösterilecek şekle (DTO'ya) dönüştürür.
    public SensorDataDto Execute(XRayMeasurement measurement)
    {
        if (measurement == null) throw new ArgumentNullException(nameof(measurement));

        return new SensorDataDto
        {
            Id = measurement.Id,
            Timestamp = measurement.Timestamp.ToLocalTime(),
            PowerW = measurement.Readings.Voltage * measurement.Readings.TubeCurrent,
            Voltage = measurement.Readings.Voltage,
            XRayIntensity = measurement.Readings.XRayIntensity,
            ExposureTime = measurement.Readings.ExposureTime,
            Frame = measurement.Frame,
            Profile = measurement.Profile,
            IsSimulator = measurement.IsSimulator,
            RequestedParameters = measurement.RequestedParameters,
            AcquisitionId = measurement.AcquisitionId,
            Operation = measurement.Operation,
            SnapFrameMode = measurement.SnapFrameMode,
            SourceWidth = measurement.SourceWidth,
            SourceHeight = measurement.SourceHeight,
            SourceMarker = measurement.SourceMarker,
            VoltageProvenance = measurement.Readings.VoltageProvenance,
            TubeCurrentProvenance = measurement.Readings.TubeCurrentProvenance,
            ExposureTimeProvenance = measurement.Readings.ExposureTimeProvenance,
            ImagePath = measurement.ImagePath,
            RawImagePath = measurement.RawImagePath
        };
    }
}
