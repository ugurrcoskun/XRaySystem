using System;

namespace XRaySystem.Domain.ValueObjects;

public enum FrameKind { LivePreview, Snap }
public enum Gray16PixelFormat { Gray16LittleEndian }
public enum AcquisitionProfile { IndustrialQaPhantom, MedicalSpecimenLikePhantom }

public sealed record AcquisitionSettings(double TubeCurrent, double Voltage, double ExposureTime, AcquisitionProfile Profile);

/// Canonical sensor-neutral representation: contiguous, top-left, one ushort per pixel.
/// Source adapters may describe their native stride/endian/orientation in the metadata.
public sealed record RawGray16Frame(
    ushort[] Pixels,
    int Width,
    int Height,
    int StrideBytes,
    Gray16PixelFormat PixelFormat,
    FrameKind Kind,
    DateTimeOffset Timestamp,
    Guid AcquisitionId,
    AcquisitionSettings Settings,
    string SourceMarker,
    int? SourceStrideBytes = null,
    string? SourceEndian = null,
    string? SourceOrientation = null)
{
    public int PixelCount => checked(Width * Height);
}
