using System;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Domain.Entities;

public class XRayMeasurement
{
    public Guid Id { get; private set; }
    public DateTime Timestamp { get; private set; }
    public SensorReadings Readings { get; private set; }
    
    // Diske kaydedilen dosyanın tam yolu
    public string? ImagePath { get; private set; }

    // Mobil standalone kaydında tam Gray16 kaynağının yolu. ImagePath low/preview olarak kalır.
    public string? RawImagePath { get; private set; }

    // Sensörden gelen ham görüntü verisi (Veritabanına kaydedilmez – DbContext'te Ignore() ile hariç tutulur)
    public ushort[]? ImageMatrix => Frame?.Pixels; // compatibility-only alias; use Frame.Pixels
    public RawGray16Frame? Frame { get; private set; }
    public AcquisitionProfile Profile { get; private set; }
    public bool IsSimulator { get; private set; }
    public AcquisitionParameters RequestedParameters { get; private set; }
    public Guid AcquisitionId { get; private set; }
    public AcquisitionOperation Operation { get; private set; }
    public SnapFrameMode? SnapFrameMode { get; private set; }
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public string SourceMarker { get; private set; }

    // EF Core için parametresiz kurucu
    protected XRayMeasurement()
    {
        Readings = null!; // EF Core materialization assigns the owned value object.
        RequestedParameters = null!;
        SourceMarker = string.Empty;
        Profile = AcquisitionProfile.IndustrialQaPhantom;
    }

    public XRayMeasurement(
        SensorReadings readings,
        RawGray16Frame frame,
        bool isSimulator = false,
        AcquisitionParameters? requestedParameters = null)
        : this(Guid.NewGuid(), frame.Timestamp.UtcDateTime, readings, frame, isSimulator, requestedParameters)
    {
    }

    public XRayMeasurement(
        Guid id,
        DateTime timestampUtc,
        SensorReadings readings,
        RawGray16Frame frame,
        bool isSimulator = false,
        AcquisitionParameters? requestedParameters = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Ölçüm ID boş olamaz.", nameof(id));
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(frame);
        Id = id;
        Timestamp = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : timestampUtc.ToUniversalTime();
        Readings = readings;
        Frame = frame;
        Profile = frame.Settings.Profile;
        IsSimulator = isSimulator;
        RequestedParameters = requestedParameters ?? new AcquisitionParameters(
            frame.Settings.Voltage,
            frame.Settings.Voltage * frame.Settings.TubeCurrent,
            frame.Settings.ExposureTime);
        RequestedParameters.EnsureValid();
        AcquisitionId = frame.AcquisitionId;
        Operation = frame.Operation;
        SnapFrameMode = frame.SnapFrameMode;
        SourceWidth = frame.Width;
        SourceHeight = frame.Height;
        SourceMarker = frame.SourceMarker;
    }

    public static XRayMeasurement CreateCatalogEntry(
        Guid id,
        DateTime timestampUtc,
        SensorReadings readings,
        AcquisitionProfile profile,
        bool isSimulator,
        AcquisitionParameters requestedParameters,
        Guid acquisitionId,
        AcquisitionOperation operation,
        SnapFrameMode? snapFrameMode,
        int sourceWidth,
        int sourceHeight,
        string? sourceMarker)
    {
        if (id == Guid.Empty) throw new ArgumentException("Ölçüm ID boş olamaz.", nameof(id));
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(requestedParameters);
        requestedParameters.EnsureValid();
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        if (acquisitionId == Guid.Empty)
            throw new ArgumentException("Acquisition ID boş olamaz.", nameof(acquisitionId));
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (operation == AcquisitionOperation.Snap &&
            (snapFrameMode is null || !Enum.IsDefined(snapFrameMode.Value)))
            throw new ArgumentException("SNAP katalog kaydı geçerli frame modu taşımalıdır.", nameof(snapFrameMode));
        if (operation != AcquisitionOperation.Snap && snapFrameMode is not null)
            throw new ArgumentException("GRAB katalog kaydı SNAP modu taşıyamaz.", nameof(snapFrameMode));
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        return new XRayMeasurement
        {
            Id = id,
            Timestamp = timestampUtc.Kind == DateTimeKind.Utc
                ? timestampUtc
                : timestampUtc.ToUniversalTime(),
            Readings = readings,
            Frame = null,
            Profile = profile,
            IsSimulator = isSimulator,
            RequestedParameters = requestedParameters,
            AcquisitionId = acquisitionId,
            Operation = operation,
            SnapFrameMode = snapFrameMode,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            SourceMarker = sourceMarker?.Trim() ?? string.Empty
        };
    }

    public void SetImagePath(string path)
    {
        ImagePath = path;
    }

    public void SetRawImagePath(string path)
    {
        RawImagePath = path;
    }
}
