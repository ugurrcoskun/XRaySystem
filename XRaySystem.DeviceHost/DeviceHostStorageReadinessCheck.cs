using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace XRaySystem.DeviceHost;

public sealed class DeviceHostStorageReadinessCheck(
    DeviceAcquisitionArchiveOptions acquisition,
    DeviceCommandAuditOptions audit) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (acquisition.Enabled)
                VerifyDirectoryWritable(acquisition.BaseFolder, "acquisition");
            if (audit.Enabled)
            {
                var auditPath = Path.GetFullPath(audit.FilePath);
                if (File.Exists(auditPath) && new FileInfo(auditPath).Length >= audit.MaximumFileBytes)
                    return Task.FromResult(HealthCheckResult.Unhealthy(
                        "Audit storage capacity limit reached."));
                var auditDirectory = Path.GetDirectoryName(auditPath)
                    ?? throw new InvalidOperationException("Audit directory unavailable.");
                VerifyDirectoryWritable(auditDirectory, "audit");
            }

            return Task.FromResult(HealthCheckResult.Healthy("Durable storage is writable."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Durable storage is unavailable.",
                ex));
        }
    }

    private static void VerifyDirectoryWritable(string directory, string purpose)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".readiness-{purpose}-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            stream.WriteByte(0x52);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }
    }
}
