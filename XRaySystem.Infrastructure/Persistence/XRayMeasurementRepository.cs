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

    public async Task<bool> ExistsAsync(Guid measurementId)
    {
        if (measurementId == Guid.Empty)
            throw new ArgumentException("Measurement ID boş olamaz.", nameof(measurementId));
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
        return await dbContext.Measurements
            .AsNoTracking()
            .AnyAsync(measurement => measurement.Id == measurementId);
    }

    public async Task DeleteAsync(Guid measurementId)
    {
        if (measurementId == Guid.Empty)
            throw new ArgumentException("Measurement ID boş olamaz.", nameof(measurementId));
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
        var measurement = await dbContext.Measurements
            .SingleOrDefaultAsync(item => item.Id == measurementId);
        if (measurement is null) return;
        dbContext.Measurements.Remove(measurement);
        await dbContext.SaveChangesAsync();
    }

    public async Task UpdateRawImagePathAsync(Guid measurementId, string rawImagePath)
    {
        if (measurementId == Guid.Empty)
            throw new ArgumentException("Measurement ID boş olamaz.", nameof(measurementId));
        ArgumentException.ThrowIfNullOrWhiteSpace(rawImagePath);
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
        var measurement = await dbContext.Measurements
            .SingleOrDefaultAsync(item => item.Id == measurementId)
            ?? throw new KeyNotFoundException("RAW yolu güncellenecek ölçüm bulunamadı.");
        measurement.SetRawImagePath(rawImagePath);
        await dbContext.SaveChangesAsync();
    }

    public async Task<int> GetCountAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
        return await dbContext.Measurements.AsNoTracking().CountAsync();
    }

    public async Task<IReadOnlyList<XRayMeasurement>> GetOldestAsync(int maximumCount)
    {
        if (maximumCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
        return await dbContext.Measurements
            .AsNoTracking()
            .OrderBy(measurement => measurement.Timestamp)
            .ThenBy(measurement => measurement.Id)
            .Take(maximumCount)
            .ToListAsync();
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
