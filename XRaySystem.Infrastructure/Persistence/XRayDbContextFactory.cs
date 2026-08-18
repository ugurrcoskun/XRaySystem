using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace XRaySystem.Infrastructure.Persistence;

public sealed class XRayDbContextFactory : IDesignTimeDbContextFactory<XRayDbContext>
{
    public XRayDbContext CreateDbContext(string[] args)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(appData, "XRaySystem");
        Directory.CreateDirectory(directory);

        var options = new DbContextOptionsBuilder<XRayDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "XRaySystem.db")}")
            .Options;
        return new XRayDbContext(options);
    }
}
