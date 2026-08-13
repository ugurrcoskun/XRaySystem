using System.Collections.Generic;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;

namespace XRaySystem.Domain.Interfaces;

public interface IXRayMeasurementRepository
{
    Task SaveAsync(XRayMeasurement measurement);
    Task<IEnumerable<XRayMeasurement>> GetAllAsync();
}
