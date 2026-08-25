using System.Threading;
using System.Threading.Tasks;
using XRaySystem.Application.DTOs;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;

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

    public async Task<XRayMeasurement> ExecuteAsync(
        SnapRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        return await _sensorService.SnapAsync(request, cancellationToken);
    }
}
