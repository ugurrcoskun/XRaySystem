using Microsoft.EntityFrameworkCore;
using XRaySystem.Domain.Entities;

namespace XRaySystem.Infrastructure.Persistence;

public class XRayDbContext : DbContext
{
    public DbSet<XRayMeasurement> Measurements { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirPath = System.IO.Path.Combine(appData, "XRaySystem");
        System.IO.Directory.CreateDirectory(dirPath);

        var dbPath = System.IO.Path.Combine(dirPath, "XRaySystem.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Value Object konfigürasyonu (Owned Type)
        modelBuilder.Entity<XRayMeasurement>().OwnsOne(x => x.Readings);
        
        modelBuilder.Entity<XRayMeasurement>().HasKey(x => x.Id);

        // ImageMatrix ham veri - DB'ye kaydedilmez (dosya yolunu ImagePath'te tutuyoruz)
        modelBuilder.Entity<XRayMeasurement>().Ignore(x => x.ImageMatrix);
        modelBuilder.Entity<XRayMeasurement>().Ignore(x => x.Frame);
    }
}
