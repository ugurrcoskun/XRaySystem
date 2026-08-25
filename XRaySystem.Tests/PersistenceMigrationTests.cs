using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Persistence;

namespace XRaySystem.Tests;

public sealed class PersistenceMigrationTests
{
    private const string RawImageMigrationId = "20260819000000_AddRawImagePath";
    private const string ProvenanceMigrationId = "20260824000000_AddAcquisitionProvenance";

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
            Assert.Contains(ProvenanceMigrationId, await db.Database.GetAppliedMigrationsAsync());
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
            Assert.Contains(ProvenanceMigrationId, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(1, await db.Measurements.CountAsync());
            var migrated = await db.Measurements.SingleAsync();
            Assert.Equal(migrated.Id, migrated.AcquisitionId);
            Assert.Equal(80, migrated.RequestedParameters.VoltageKv);
            Assert.Equal(200, migrated.RequestedParameters.PowerW);
            Assert.Equal(100, migrated.RequestedParameters.ExposureTimeMs);
            Assert.Equal("legacy-history", migrated.SourceMarker);

            await using var verify = new SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            var command = verify.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Measurements') " +
                "WHERE name IN ('ImagePath', 'RawImagePath', 'Profile', 'IsSimulator', " +
                "'AcquisitionId', 'Operation', 'SnapFrameMode', 'SourceWidth', 'SourceHeight', " +
                "'SourceMarker', 'RequestedParameters_VoltageKv', 'RequestedParameters_PowerW', " +
                "'RequestedParameters_ExposureTimeMs', 'Readings_TubeCurrentProvenance', " +
                "'Readings_VoltageProvenance', 'Readings_ExposureTimeProvenance');";
            Assert.Equal(16L, (long)(await command.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Acquisition_metadata_and_target_measured_provenance_round_trip()
    {
        await InTemporaryDatabaseAsync(async path =>
        {
            var acquisitionId = Guid.NewGuid();
            await using (var db = CreateContext(path))
            {
                await db.Database.MigrateAsync();
                var frame = new RawGray16Frame(
                    [1, 2, 3, 4], 2, 2, 4,
                    Gray16PixelFormat.Gray16LittleEndian,
                    FrameKind.Snap,
                    DateTimeOffset.UtcNow,
                    acquisitionId,
                    new AcquisitionSettings(2.4, 79.5, 100, AcquisitionProfile.MedicalSpecimenLikePhantom),
                    "roundtrip-detector",
                    operation: AcquisitionOperation.Snap,
                    snapFrameMode: SnapFrameMode.Low);
                var measurement = new XRayMeasurement(
                    new SensorReadings(
                        2.4,
                        79.5,
                        1.2,
                        99.8,
                        MeasurementProvenance.Measured,
                        MeasurementProvenance.Measured,
                        MeasurementProvenance.Measured),
                    frame,
                    isSimulator: false,
                    requestedParameters: new AcquisitionParameters(80, 200, 100));
                db.Measurements.Add(measurement);
                await db.SaveChangesAsync();
            }

            await using var verify = CreateContext(path);
            var stored = await verify.Measurements.AsNoTracking().SingleAsync();
            Assert.Equal(acquisitionId, stored.AcquisitionId);
            Assert.Equal(AcquisitionOperation.Snap, stored.Operation);
            Assert.Equal(SnapFrameMode.Low, stored.SnapFrameMode);
            Assert.Equal(2, stored.SourceWidth);
            Assert.Equal(2, stored.SourceHeight);
            Assert.Equal("roundtrip-detector", stored.SourceMarker);
            Assert.Equal(80, stored.RequestedParameters.VoltageKv);
            Assert.Equal(200, stored.RequestedParameters.PowerW);
            Assert.Equal(100, stored.RequestedParameters.ExposureTimeMs);
            Assert.Equal(MeasurementProvenance.Measured, stored.Readings.VoltageProvenance);
            Assert.Equal(MeasurementProvenance.Measured, stored.Readings.TubeCurrentProvenance);
            Assert.Equal(MeasurementProvenance.Measured, stored.Readings.ExposureTimeProvenance);
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
