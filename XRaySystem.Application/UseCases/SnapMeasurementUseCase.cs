using System.Threading;
using System.Threading.Tasks;
using XRaySystem.Application.DTOs;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.Entities;

namespace XRaySystem.Application.UseCases;

public class SnapMeasurementUseCase
{
    private readonly ISensorService _sensorService;

    public SnapMeasurementUseCase(ISensorService sensorService)
    {
        _sensorService = sensorService;
    }

    public async Task<XRayMeasurement> ExecuteAsync(double exposureTime, CancellationToken cancellationToken = default)
    {
        return await _sensorService.SnapAsync(exposureTime, cancellationToken);
    }
}
