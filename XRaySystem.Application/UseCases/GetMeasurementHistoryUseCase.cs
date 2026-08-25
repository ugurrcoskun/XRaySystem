using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XRaySystem.Application.DTOs;
using XRaySystem.Domain.Interfaces;

namespace XRaySystem.Application.UseCases;

public class GetMeasurementHistoryUseCase
{
    private readonly IXRayMeasurementRepository _repository;
    private readonly ProcessTelemetryUseCase _processTelemetryUseCase;

    public GetMeasurementHistoryUseCase(
        IXRayMeasurementRepository repository, 
        ProcessTelemetryUseCase processTelemetryUseCase)
    {
        _repository = repository;
        _processTelemetryUseCase = processTelemetryUseCase;
    }

    public async Task<IReadOnlyList<SensorDataDto>> ExecuteAsync(int maximumCount = 200)
    {
        if (maximumCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        var entities = await _repository.GetRecentAsync(maximumCount);
        return entities.Select(e => _processTelemetryUseCase.Execute(e)).ToList();
    }
}
