using System;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;

namespace XRaySystem.Domain.Interfaces;

public interface ISensorService
{
    DeviceStatus CurrentStatus { get; }
    
    // Durum değiştiğinde fırlatılacak olay (Bağlandı, Koptu vs.)
    event Action<DeviceStatus>? StatusChanged;
    
    // Yeni bir veri okunduğunda fırlatılacak olay
    event Action<XRayMeasurement>? MeasurementReceived;

    Task ConnectAsync();
    Task DisconnectAsync();
    Task StartCaptureAsync();
    Task StopCaptureAsync();
    Task<XRayMeasurement> SnapAsync(double exposureTime);
    Task ResumeLiveAsync();
}
