using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;

using Microsoft.Extensions.DependencyInjection;

namespace XRaySystem.Infrastructure.Persistence;

public class XRayMeasurementRepository : IXRayMeasurementRepository
{
    private readonly IServiceScopeFactory _scopeFactory;

    public XRayMeasurementRepository(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task SaveAsync(XRayMeasurement measurement)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
        
        // Prevent EF from tracking it as an existing entity, just add it.
        dbContext.Measurements.Add(measurement);
        await dbContext.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<XRayMeasurement>> GetRecentAsync(int maximumCount)
    {
        if (maximumCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XRayDbContext>();

        // En son kaydedilenden eskiye doğru listele
        return await dbContext.Measurements
            .AsNoTracking()
            .OrderByDescending(m => m.Timestamp)
            .Take(maximumCount)
            .ToListAsync();
    }
}
