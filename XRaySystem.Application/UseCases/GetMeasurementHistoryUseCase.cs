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

    public async Task<IEnumerable<SensorDataDto>> ExecuteAsync()
    {
        var entities = await _repository.GetAllAsync();
        return entities.Select(e => _processTelemetryUseCase.Execute(e)).ToList();
    }
}
