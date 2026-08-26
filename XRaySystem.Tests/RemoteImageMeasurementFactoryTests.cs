using XRaySystem.Contracts;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;
using XRaySystem.RemoteClient;

namespace XRaySystem.Tests;

public sealed class RemoteImageMeasurementFactoryTests
{
    [Fact]
    public void Verified_remote_raw_is_reconstructed_with_catalog_identity()
    {
        var frame = CreateFrame();
        var metadata = CreateMetadata(frame);

        var measurement = RemoteImageMeasurementFactory.Create(metadata, Encode(frame));

        Assert.Equal(metadata.ImageId, measurement.Id);
        Assert.Equal(metadata.AcquisitionId, measurement.AcquisitionId);
        Assert.Equal(frame.Pixels, measurement.Frame!.Pixels);
        Assert.True(measurement.IsSimulator);
        Assert.Equal(MeasurementProvenance.Measured, measurement.Readings.VoltageProvenance);
    }

    [Fact]
    public void Catalog_and_raw_dimension_mismatch_is_rejected()
    {
        var frame = CreateFrame();
        var metadata = CreateMetadata(frame) with { Width = frame.Width + 1 };

        Assert.Throws<InvalidDataException>(() =>
            RemoteImageMeasurementFactory.Create(metadata, Encode(frame)));
    }

    [Fact]
    public void Catalog_entry_can_be_persisted_without_downloading_raw_pixels()
    {
        var frame = CreateFrame();
        var metadata = CreateMetadata(frame);

        var measurement = RemoteImageMeasurementFactory.CreateCatalogEntry(metadata);

        Assert.Null(measurement.Frame);
        Assert.Equal(metadata.ImageId, measurement.Id);
        Assert.Equal(metadata.AcquisitionId, measurement.AcquisitionId);
        Assert.Equal(metadata.Width, measurement.SourceWidth);
        Assert.Equal(metadata.Height, measurement.SourceHeight);
        Assert.Equal("remote-device-catalog:simulation", measurement.SourceMarker);
    }

    private static RawGray16Frame CreateFrame()
    {
        var timestamp = new DateTimeOffset(2026, 8, 25, 12, 30, 0, TimeSpan.Zero);
        return new RawGray16Frame(
            [10, 20, 30, 40],
            2,
            2,
            4,
            Gray16PixelFormat.Gray16LittleEndian,
            FrameKind.Snap,
            timestamp,
            Guid.NewGuid(),
            new AcquisitionSettings(3, 70, 200, AcquisitionProfile.IndustrialQaPhantom),
            "mock-xray-sensor",
            sourceOrientation: "top-left",
            operation: AcquisitionOperation.Snap,
            snapFrameMode: SnapFrameMode.Raw);
    }

    private static RemoteImageMetadataContract CreateMetadata(RawGray16Frame frame) => new(
        Guid.NewGuid(),
        frame.AcquisitionId,
        frame.Kind.ToString(),
        frame.Width,
        frame.Height,
        frame.StrideBytes,
        frame.PixelFormat.ToString(),
        2,
        2,
        frame.Timestamp,
        frame.Settings.Profile.ToString(),
        new AcquisitionParametersContract(70, 210, 200),
        new MeasuredTelemetryContract(
            70,
            3,
            210,
            42,
            200,
            MeasurementProvenanceContract.Measured,
            MeasurementProvenanceContract.Measured,
            MeasurementProvenanceContract.Measured),
        LowAvailable: true,
        RawAvailable: true,
        Operation: AcquisitionOperationContract.Snap,
        SnapFrameMode: SnapFrameModeContract.Raw,
        PersistedOnDevice: true,
        IsSimulator: true);

    private static byte[] Encode(RawGray16Frame frame)
    {
        using var stream = new MemoryStream();
        Gray16RawFileCodec.Write(stream, frame);
        return stream.ToArray();
    }
}
