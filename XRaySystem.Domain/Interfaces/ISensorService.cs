using System;
using System.Threading;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Domain.Interfaces;

public interface ISensorService
{
    DeviceStatus CurrentStatus { get; }
    
    // Durum değiştiğinde fırlatılacak olay (Bağlandı, Koptu vs.)
    event Action<DeviceStatus>? StatusChanged;
    
    // Yeni bir veri okunduğunda fırlatılacak olay
    event Action<XRayMeasurement>? MeasurementReceived;
    AcquisitionProfile Profile { get; set; }
    AcquisitionParameters Parameters { get; set; }

    Task ConnectAsync();
    Task DisconnectAsync();
    Task StartCaptureAsync();
    Task StopCaptureAsync();
    Task<XRayMeasurement> SnapAsync(double exposureTime, CancellationToken cancellationToken = default);
    Task ResumeLiveAsync();
}
