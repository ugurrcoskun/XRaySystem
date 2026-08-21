using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XRaySystem.Infrastructure.Persistence;

namespace XRaySystem.Tests;

public sealed class PersistenceMigrationTests
{
    private const string RawImageMigrationId = "20260819000000_AddRawImagePath";

    [Fact]
    public async Task Fresh_database_is_created_by_initial_migration()
    {
        await InTemporaryDatabaseAsync(async path =>
        {
            await using var db = CreateContext(path);
            LegacyDatabaseBaseliner.Prepare(db);
            await db.Database.MigrateAsync();

            Assert.Contains(LegacyDatabaseBaseliner.InitialMigrationId, await db.Database.GetAppliedMigrationsAsync());
            Assert.Contains(RawImageMigrationId, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(0, await db.Measurements.CountAsync());
        });
    }

    [Fact]
    public async Task Legacy_database_is_baselined_without_losing_measurements()
    {
        await InTemporaryDatabaseAsync(async path =>
        {
            await CreateLegacyDatabaseAsync(path);

            await using var db = CreateContext(path);
            LegacyDatabaseBaseliner.Prepare(db);
            await db.Database.MigrateAsync();

            Assert.Contains(LegacyDatabaseBaseliner.InitialMigrationId, await db.Database.GetAppliedMigrationsAsync());
            Assert.Contains(RawImageMigrationId, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(1, await db.Measurements.CountAsync());

            await using var verify = new SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            var command = verify.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Measurements') " +
                "WHERE name IN ('ImagePath', 'RawImagePath', 'Profile', 'IsSimulator');";
            Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
        });
    }

    private static XRayDbContext CreateContext(string path) =>
        new(new DbContextOptionsBuilder<XRayDbContext>().UseSqlite($"Data Source={path}").Options);

    private static async Task CreateLegacyDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Measurements (
                Id TEXT NOT NULL PRIMARY KEY,
                Timestamp TEXT NOT NULL,
                Readings_TubeCurrent REAL NOT NULL,
                Readings_Voltage REAL NOT NULL,
                Readings_XRayIntensity REAL NOT NULL,
                Readings_ExposureTime REAL NOT NULL
            );
            INSERT INTO Measurements VALUES (
                '00000000-0000-0000-0000-000000000001',
                '2026-08-18 12:00:00', 2.5, 80.0, 1.0, 100.0
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InTemporaryDatabaseAsync(Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xray-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await test(Path.Combine(directory, "test.db")); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
