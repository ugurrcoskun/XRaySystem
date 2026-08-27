using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Persistence;

namespace XRaySystem.Tests;

public sealed class BoundedMeasurementFileCacheTests
{
    [Fact]
    public async Task Cache_removes_only_the_oldest_records_and_their_local_files()
    {
        var testRoot = CreateTestRoot();
        var cacheRoot = Path.Combine(testRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var measurements = Enumerable.Range(0, 32)
                .Select(index => CreateMeasurement(cacheRoot, index))
                .ToList();
            var repository = new MemoryRepository(measurements);
            var cache = new BoundedMeasurementFileCache(repository, cacheRoot);

            var removed = await cache.EnforceAsync(keepCount: 30);

            Assert.Equal(2, removed);
            Assert.Equal(30, repository.Items.Count);
            foreach (var expired in measurements.OrderByDescending(item => item.Timestamp).Skip(30))
            {
                Assert.DoesNotContain(repository.Items, item => item.Id == expired.Id);
                Assert.False(File.Exists(expired.ImagePath));
                Assert.False(File.Exists(expired.RawImagePath));
            }
            foreach (var retained in measurements.OrderByDescending(item => item.Timestamp).Take(30))
            {
                Assert.Contains(repository.Items, item => item.Id == retained.Id);
                Assert.True(File.Exists(retained.ImagePath));
                Assert.True(File.Exists(retained.RawImagePath));
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cache_refuses_to_delete_a_file_outside_its_mobile_root()
    {
        var testRoot = CreateTestRoot();
        var cacheRoot = Path.Combine(testRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        var outsidePath = Path.Combine(testRoot, "outside.bmp");
        File.WriteAllBytes(outsidePath, [1]);
        try
        {
            var newest = CreateMeasurement(cacheRoot, 1);
            var expired = CreateMeasurement(cacheRoot, 0);
            expired.SetImagePath(outsidePath);
            var repository = new MemoryRepository([newest, expired]);
            var cache = new BoundedMeasurementFileCache(repository, cacheRoot);

            await Assert.ThrowsAsync<InvalidDataException>(() => cache.EnforceAsync(keepCount: 1));

            Assert.True(File.Exists(outsidePath));
            Assert.Contains(repository.Items, item => item.Id == expired.Id);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cache_enforces_the_limit_even_when_legacy_history_exceeds_five_hundred_rows()
    {
        var testRoot = CreateTestRoot();
        var cacheRoot = Path.Combine(testRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var measurements = Enumerable.Range(0, 532)
                .Select(index => CreateMeasurement(cacheRoot, index))
                .ToList();
            var repository = new MemoryRepository(measurements);
            var cache = new BoundedMeasurementFileCache(repository, cacheRoot);

            var removed = await cache.EnforceAsync(keepCount: 30);

            Assert.Equal(502, removed);
            Assert.Equal(30, repository.Items.Count);
            Assert.All(
                measurements.OrderByDescending(item => item.Timestamp).Take(30),
                retained => Assert.Contains(repository.Items, item => item.Id == retained.Id));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Startup_reconciliation_removes_broken_rows_and_quarantines_orphan_files()
    {
        var testRoot = CreateTestRoot();
        var cacheRoot = Path.Combine(testRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var valid = CreateMeasurement(cacheRoot, 1);
            var broken = CreateMeasurement(cacheRoot, 0);
            File.Delete(broken.ImagePath!);
            File.Delete(broken.RawImagePath!);
            var orphanPath = Path.Combine(cacheRoot, "orphan.bmp");
            File.WriteAllBytes(orphanPath, [(byte)'B', (byte)'M', 0, 0]);
            var repository = new MemoryRepository([valid, broken]);
            var cache = new BoundedMeasurementFileCache(repository, cacheRoot);

            var result = await cache.ReconcileAsync();

            Assert.Equal(1, result.RemovedBrokenRecords);
            Assert.Equal(1, result.QuarantinedOrphanFiles);
            Assert.Contains(repository.Items, item => item.Id == valid.Id);
            Assert.DoesNotContain(repository.Items, item => item.Id == broken.Id);
            Assert.False(File.Exists(orphanPath));
            Assert.True(File.Exists(Path.Combine(cacheRoot, "Recovery", "orphan.bmp")));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xraysystem-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static XRayMeasurement CreateMeasurement(string cacheRoot, int minuteOffset)
    {
        var timestamp = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero)
            .AddMinutes(minuteOffset);
        var frame = new RawGray16Frame(
            [checked((ushort)(minuteOffset + 1))],
            1,
            1,
            sizeof(ushort),
            Gray16PixelFormat.Gray16LittleEndian,
            FrameKind.Snap,
            timestamp,
            Guid.NewGuid(),
            new AcquisitionSettings(5, 80, 100, AcquisitionProfile.IndustrialQaPhantom),
            "unit-test",
            operation: AcquisitionOperation.Snap,
            snapFrameMode: SnapFrameMode.Raw);
        var measurement = new XRayMeasurement(
            Guid.NewGuid(),
            timestamp.UtcDateTime,
            new SensorReadings(5, 80, 0, 100),
            frame,
            isSimulator: true);
        var imagePath = Path.Combine(cacheRoot, $"{measurement.Id:N}.bmp");
        var rawPath = Path.Combine(cacheRoot, $"{measurement.Id:N}.tiff");
        File.WriteAllBytes(imagePath, [1]);
        File.WriteAllBytes(rawPath, [2]);
        measurement.SetImagePath(imagePath);
        measurement.SetRawImagePath(rawPath);
        return measurement;
    }

    private sealed class MemoryRepository(IEnumerable<XRayMeasurement> measurements)
        : IXRayMeasurementRepository
    {
        public List<XRayMeasurement> Items { get; } = [.. measurements];

        public Task SaveAsync(XRayMeasurement measurement)
        {
            Items.Add(measurement);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid measurementId)
        {
            Items.RemoveAll(item => item.Id == measurementId);
            return Task.CompletedTask;
        }

        public Task<int> GetCountAsync() => Task.FromResult(Items.Count);

        public Task<IReadOnlyList<XRayMeasurement>> GetOldestAsync(int maximumCount) =>
            Task.FromResult<IReadOnlyList<XRayMeasurement>>(
                [.. Items
                    .OrderBy(item => item.Timestamp)
                    .ThenBy(item => item.Id)
                    .Take(maximumCount)]);

        public Task<IReadOnlyList<XRayMeasurement>> GetRecentAsync(int maximumCount) =>
            Task.FromResult<IReadOnlyList<XRayMeasurement>>(
                [.. Items.OrderByDescending(item => item.Timestamp).Take(maximumCount)]);
    }
}
