using System.Threading.Tasks;
using XRaySystem.Domain.Entities;

namespace XRaySystem.Domain.Interfaces;

public interface ICloudPublisher
{
    Task PublishMeasurementAsync(XRayMeasurement measurement);
    Task PublishStatusAsync(DeviceStatus status);
}
