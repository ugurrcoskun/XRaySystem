using XRaySystem.Application.UseCases;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Tests;

public sealed class ProcessTelemetryUseCaseTests
{
    [Fact]
    public void Telemetry_exposes_power_in_watt()
    {
        var readings = new SensorReadings(5, 80, 300, 33);
        var frame = new RawGray16Frame(
            [123], 1, 1, 2,
            Gray16PixelFormat.Gray16LittleEndian,
            FrameKind.LivePreview,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            new AcquisitionSettings(5, 80, 33, AcquisitionProfile.IndustrialQaPhantom),
            "unit-test");
        var measurement = new XRayMeasurement(readings, frame, isSimulator: true);
        measurement.SetImagePath("preview.bmp");
        measurement.SetRawImagePath("source.xrayraw");

        var dto = new ProcessTelemetryUseCase().Execute(measurement);

        Assert.Equal(400, dto.PowerW);
        Assert.Equal(80, dto.Voltage);
        Assert.True(dto.IsSimulator);
        Assert.Equal("preview.bmp", dto.ImagePath);
        Assert.Equal("source.xrayraw", dto.RawImagePath);
        Assert.Equal(frame.AcquisitionId, dto.AcquisitionId);
        Assert.Equal(AcquisitionOperation.Grab, dto.Operation);
        Assert.Equal(frame.Width, dto.SourceWidth);
        Assert.Equal(frame.SourceMarker, dto.SourceMarker);
        Assert.Equal(80, dto.RequestedParameters!.VoltageKv);
    }
}
