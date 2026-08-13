using System;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XRaySystem.Domain.Interfaces;

class Program {
    static async Task Main() {
        var services = new ServiceCollection();
        services.AddDbContext<XRayDbContext>();
        services.AddSingleton<IXRayMeasurementRepository, XRayMeasurementRepository>();
        var sp = services.BuildServiceProvider();
        
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
        db.Database.EnsureCreated();
        
        var repo = scope.ServiceProvider.GetRequiredService<IXRayMeasurementRepository>();
        
        try {
            var m = new XRayMeasurement(new SensorReadings(1,2,3,4), new ushort[2324 * 2930]);
            await repo.SaveAsync(m);
            Console.WriteLine("SUCCESS!");
        } catch (Exception ex) {
            Console.WriteLine("ERROR: " + ex.ToString());
        }
    }
}
