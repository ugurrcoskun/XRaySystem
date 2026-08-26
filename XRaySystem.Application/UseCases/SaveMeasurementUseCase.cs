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

    public async Task<bool> ExecuteIfMissingAsync(XRayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (await _repository.ExistsAsync(measurement.Id).ConfigureAwait(false)) return false;
        await _repository.SaveAsync(measurement).ConfigureAwait(false);
        return true;
    }

    public Task UpdateRawImagePathAsync(Guid measurementId, string rawImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawImagePath);
        return _repository.UpdateRawImagePathAsync(measurementId, rawImagePath);
    }
}
