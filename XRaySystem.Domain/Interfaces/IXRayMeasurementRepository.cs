using System.Collections.Generic;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;

namespace XRaySystem.Domain.Interfaces;

public interface IXRayMeasurementRepository
{
    Task SaveAsync(XRayMeasurement measurement);
    Task<bool> ExistsAsync(Guid measurementId) => Task.FromResult(false);
    Task DeleteAsync(Guid measurementId) => Task.CompletedTask;
    Task UpdateRawImagePathAsync(Guid measurementId, string rawImagePath) => Task.CompletedTask;
    async Task<int> GetCountAsync() => (await GetRecentAsync(500)).Count;
    async Task<IReadOnlyList<XRayMeasurement>> GetOldestAsync(int maximumCount) =>
        (await GetRecentAsync(500))
        .OrderBy(measurement => measurement.Timestamp)
        .ThenBy(measurement => measurement.Id)
        .Take(maximumCount)
        .ToArray();
    Task<IReadOnlyList<XRayMeasurement>> GetRecentAsync(int maximumCount);
}
