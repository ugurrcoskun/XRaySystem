using XRaySystem.Contracts;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;

namespace XRaySystem.RemoteClient;

/// <summary>
/// Reconstructs a domain measurement only after the downloaded Gray16 artifact
/// and the authoritative DeviceHost catalog entry agree.
/// </summary>
public static class RemoteImageMeasurementFactory
{
    public static XRayMeasurement CreateCatalogEntry(RemoteImageMetadataContract metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.ImageId == Guid.Empty || metadata.AcquisitionId == Guid.Empty ||
            metadata.Operation != AcquisitionOperationContract.Snap ||
            metadata.SnapFrameMode is null ||
            metadata.Width <= 0 || metadata.Height <= 0 ||
            metadata.StrideBytes != checked(metadata.Width * sizeof(ushort)) ||
            metadata.LowWidth <= 0 || metadata.LowHeight <= 0 ||
            !string.Equals(metadata.Kind, FrameKind.Snap.ToString(), StringComparison.Ordinal) ||
            !string.Equals(metadata.PixelFormat, Gray16PixelFormat.Gray16LittleEndian.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException("Remote katalog SNAP metadata bilgisi geçersiz.");
        if (!Enum.TryParse<AcquisitionProfile>(metadata.Profile, true, out var profile) ||
            !Enum.IsDefined(profile))
            throw new InvalidDataException("Remote katalog acquisition profili geçersiz.");

        var measured = metadata.MeasuredTelemetry;
        return XRayMeasurement.CreateCatalogEntry(
            metadata.ImageId,
            metadata.CapturedAtUtc.UtcDateTime,
            new SensorReadings(
                measured.TubeCurrentMa,
                measured.VoltageKv,
                measured.XRayIntensity,
                measured.ExposureTimeMs,
                ToDomain(measured.TubeCurrentProvenance),
                ToDomain(measured.VoltageProvenance),
                ToDomain(measured.ExposureTimeProvenance)),
            profile,
            metadata.IsSimulator == true,
            new AcquisitionParameters(
                metadata.RequestedParameters.VoltageKv,
                metadata.RequestedParameters.PowerW,
                metadata.RequestedParameters.ExposureTimeMs),
            metadata.AcquisitionId,
            AcquisitionOperation.Snap,
            metadata.SnapFrameMode == SnapFrameModeContract.Low
                ? SnapFrameMode.Low
                : SnapFrameMode.Raw,
            metadata.Width,
            metadata.Height,
            metadata.IsSimulator switch
            {
                true => "remote-device-catalog:simulation",
                false => "remote-device-catalog:device",
                null => null
            });
    }

    public static XRayMeasurement Create(
        RemoteImageMetadataContract metadata,
        byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rawBytes);
        if (rawBytes.Length == 0)
            throw new InvalidDataException("Remote RAW görüntü boş.");

        RawGray16Frame frame;
        using (var stream = new MemoryStream(rawBytes, writable: false))
            frame = Gray16RawFileCodec.Read(stream);
        EnsureMatches(frame, metadata);

        var measured = metadata.MeasuredTelemetry;
        return new XRayMeasurement(
            metadata.ImageId,
            metadata.CapturedAtUtc.UtcDateTime,
            new SensorReadings(
                measured.TubeCurrentMa,
                measured.VoltageKv,
                measured.XRayIntensity,
                measured.ExposureTimeMs,
                ToDomain(measured.TubeCurrentProvenance),
                ToDomain(measured.VoltageProvenance),
                ToDomain(measured.ExposureTimeProvenance)),
            frame,
            metadata.IsSimulator == true,
            new AcquisitionParameters(
                metadata.RequestedParameters.VoltageKv,
                metadata.RequestedParameters.PowerW,
                metadata.RequestedParameters.ExposureTimeMs));
    }

    public static void EnsureMatches(
        RawGray16Frame frame,
        RemoteImageMetadataContract metadata)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(metadata);
        var expectedSnapMode = metadata.SnapFrameMode == SnapFrameModeContract.Low
            ? SnapFrameMode.Low
            : SnapFrameMode.Raw;
        if (metadata.ImageId == Guid.Empty ||
            frame.AcquisitionId != metadata.AcquisitionId ||
            frame.Width != metadata.Width ||
            frame.Height != metadata.Height ||
            frame.StrideBytes != metadata.StrideBytes ||
            !string.Equals(frame.PixelFormat.ToString(), metadata.PixelFormat, StringComparison.Ordinal) ||
            !string.Equals(frame.Kind.ToString(), metadata.Kind, StringComparison.Ordinal) ||
            !string.Equals(frame.Settings.Profile.ToString(), metadata.Profile, StringComparison.Ordinal) ||
            metadata.Operation != AcquisitionOperationContract.Snap ||
            metadata.SnapFrameMode is null ||
            frame.Operation != AcquisitionOperation.Snap ||
            frame.SnapFrameMode != expectedSnapMode)
            throw new InvalidDataException("Remote RAW görüntü ve DeviceHost katalog metadata bilgisi uyuşmuyor.");
    }

    private static MeasurementProvenance ToDomain(MeasurementProvenanceContract provenance) =>
        provenance switch
        {
            MeasurementProvenanceContract.Measured => MeasurementProvenance.Measured,
            MeasurementProvenanceContract.AppliedTarget => MeasurementProvenance.AppliedTarget,
            MeasurementProvenanceContract.Derived => MeasurementProvenance.Derived,
            _ => MeasurementProvenance.Unknown
        };
}
