using System;

namespace XRaySystem.Domain.ValueObjects;

public enum FrameKind { LivePreview, Snap }
public enum Gray16PixelFormat { Gray16LittleEndian }
public enum AcquisitionProfile { IndustrialQaPhantom, MedicalSpecimenLikePhantom }

public sealed record AcquisitionSettings(double TubeCurrent, double Voltage, double ExposureTime, AcquisitionProfile Profile);

/// Canonical sensor-neutral representation: contiguous, top-left, one ushort per pixel.
/// Source adapters may describe their native stride/endian/orientation in the metadata.
public sealed record RawGray16Frame
{
    public ushort[] Pixels { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int StrideBytes { get; init; }
    public Gray16PixelFormat PixelFormat { get; init; }
    public FrameKind Kind { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Guid AcquisitionId { get; init; }
    public AcquisitionSettings Settings { get; init; }
    public string SourceMarker { get; init; }
    public int? SourceStrideBytes { get; init; }
    public string? SourceEndian { get; init; }
    public string? SourceOrientation { get; init; }

    public RawGray16Frame(
        ushort[] pixels,
        int width,
        int height,
        int strideBytes,
        Gray16PixelFormat pixelFormat,
        FrameKind kind,
        DateTimeOffset timestamp,
        Guid acquisitionId,
        AcquisitionSettings settings,
        string sourceMarker,
        int? sourceStrideBytes = null,
        string? sourceEndian = null,
        string? sourceOrientation = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(settings);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var expectedPixelCount = checked(width * height);
        var expectedStride = checked(width * sizeof(ushort));
        if (pixels.Length != expectedPixelCount)
            throw new ArgumentException(
                $"Pixel count {pixels.Length}, frame dimensions {width}x{height} için beklenen {expectedPixelCount} değeriyle uyuşmuyor.",
                nameof(pixels));
        if (strideBytes != expectedStride)
            throw new ArgumentException(
                $"Canonical Gray16 stride {expectedStride} byte olmalıdır.",
                nameof(strideBytes));
        if (!Enum.IsDefined(pixelFormat))
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (sourceStrideBytes is { } nativeStride && nativeStride < expectedStride)
            throw new ArgumentOutOfRangeException(
                nameof(sourceStrideBytes),
                $"Source stride en az {expectedStride} byte olmalıdır.");
        if (sourceEndian is not null &&
            !sourceEndian.Equals("little", StringComparison.OrdinalIgnoreCase) &&
            !sourceEndian.Equals("big", StringComparison.OrdinalIgnoreCase) &&
            !sourceEndian.Equals("little-endian", StringComparison.OrdinalIgnoreCase) &&
            !sourceEndian.Equals("big-endian", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source endian little veya big olarak belirtilmelidir.", nameof(sourceEndian));
        if (acquisitionId == Guid.Empty)
            throw new ArgumentException("Acquisition ID boş olamaz.", nameof(acquisitionId));
        if (string.IsNullOrWhiteSpace(sourceMarker))
            throw new ArgumentException("Frame source marker boş olamaz.", nameof(sourceMarker));

        Pixels = pixels;
        Width = width;
        Height = height;
        StrideBytes = strideBytes;
        PixelFormat = pixelFormat;
        Kind = kind;
        Timestamp = timestamp;
        AcquisitionId = acquisitionId;
        Settings = settings;
        SourceMarker = sourceMarker;
        SourceStrideBytes = sourceStrideBytes;
        SourceEndian = sourceEndian;
        SourceOrientation = sourceOrientation;
    }

    public int PixelCount => checked(Width * Height);
}
