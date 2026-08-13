using System.Threading.Tasks;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;

namespace XRaySystem.Infrastructure.Cloud;

public class NullCloudPublisher : ICloudPublisher
{
    // Bu sınıf bilerek hiçbir şey yapmaz (Null Object Pattern).
    // İleride Firebase eklenene kadar uygulamanın hata vermeden (çökmeksizin) çalışmasını sağlar.

    public Task PublishMeasurementAsync(XRayMeasurement measurement)
    {
        return Task.CompletedTask;
    }

    public Task PublishStatusAsync(DeviceStatus status)
    {
        return Task.CompletedTask;
    }
}
