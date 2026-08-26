using XRaySystem.Domain.Interfaces;

namespace XRaySystem.Infrastructure.Persistence;

public sealed record MeasurementCacheReconciliationResult(
    int RemovedBrokenRecords,
    int QuarantinedOrphanFiles);

/// <summary>
/// Bounds a client-side measurement cache without ever issuing a delete to the
/// authoritative DeviceHost archive. Files are deleted only when they resolve
/// beneath the explicitly configured cache root.
/// </summary>
public sealed class BoundedMeasurementFileCache
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly IXRayMeasurementRepository _repository;
    private readonly string _cacheRoot;
    private readonly string _cacheRootWithSeparator;

    public BoundedMeasurementFileCache(
        IXRayMeasurementRepository repository,
        string cacheRoot)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        var fullRoot = Path.GetFullPath(cacheRoot);
        _cacheRoot = fullRoot;
        _cacheRootWithSeparator = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public async Task<MeasurementCacheReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheRoot);
        var history = await _repository.GetRecentAsync(500).ConfigureAwait(false);
        var referenced = new HashSet<string>(PathComparer);
        var removedBrokenRecords = 0;
        foreach (var measurement in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = new[] { measurement.ImagePath, measurement.RawImagePath }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => EnsureCachePath(path!))
                .Distinct(PathComparer)
                .ToArray();
            foreach (var path in paths) referenced.Add(path);
            if (paths.Any(File.Exists)) continue;
            await _repository.DeleteAsync(measurement.Id).ConfigureAwait(false);
            removedBrokenRecords++;
        }

        var candidates = Directory
            .EnumerateFiles(_cacheRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                Path.GetExtension(path).Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".xrayraw", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                File.GetLastWriteTimeUtc(path) <= DateTime.UtcNow.AddMinutes(-10))
            .Where(path => !referenced.Contains(Path.GetFullPath(path)))
            .ToArray();
        var quarantined = 0;
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var recoveryFolder = Path.Combine(_cacheRoot, "Recovery");
                Directory.CreateDirectory(recoveryFolder);
                var destination = Path.Combine(recoveryFolder, Path.GetFileName(path));
                if (File.Exists(destination))
                    destination = Path.Combine(
                        recoveryFolder,
                        $"{Path.GetFileNameWithoutExtension(path)}_{Guid.NewGuid():N}{Path.GetExtension(path)}");
                File.Move(path, destination);
                quarantined++;
            }
            catch (IOException)
            {
                // Leave the orphan in place so the next startup can retry safely.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave the orphan in place so the next startup can retry safely.
            }
        }

        return new MeasurementCacheReconciliationResult(removedBrokenRecords, quarantined);
    }

    public async Task<int> EnforceAsync(
        int keepCount,
        CancellationToken cancellationToken = default)
    {
        if (keepCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(keepCount));
        var removed = 0;
        while (await _repository.GetCountAsync().ConfigureAwait(false) > keepCount)
        {
            var excessCount = await _repository.GetCountAsync().ConfigureAwait(false) - keepCount;
            var expired = await _repository
                .GetOldestAsync(Math.Min(100, excessCount))
                .ConfigureAwait(false);
            if (expired.Count == 0) break;
            var removedThisBatch = 0;
            foreach (var measurement in expired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var paths = new[] { measurement.ImagePath, measurement.RawImagePath }
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => EnsureCachePath(path!))
                    .Distinct(PathComparer)
                    .ToArray();
                try
                {
                    foreach (var path in paths)
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                await _repository.DeleteAsync(measurement.Id).ConfigureAwait(false);
                removed++;
                removedThisBatch++;
            }

            // Locked files remain represented by their DB rows and are retried on
            // the next maintenance pass; avoid spinning forever in this pass.
            if (removedThisBatch == 0) break;
        }
        return removed;
    }

    private string EnsureCachePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_cacheRootWithSeparator, PathComparison))
            throw new InvalidDataException("Cache kaydı izin verilen mobil uygulama klasörünün dışında.");
        return fullPath;
    }
}
