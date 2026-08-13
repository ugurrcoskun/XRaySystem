using System.Threading.Tasks;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;

namespace XRaySystem.Application.UseCases;

public class SaveMeasurementUseCase
{
    private readonly IXRayMeasurementRepository _repository;

    public SaveMeasurementUseCase(IXRayMeasurementRepository repository)
    {
        _repository = repository;
    }

    public async Task ExecuteAsync(XRayMeasurement measurement)
    {
        if (measurement != null)
        {
            await _repository.SaveAsync(measurement);
        }
    }
}
