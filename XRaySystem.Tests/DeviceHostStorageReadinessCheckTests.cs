using Microsoft.Extensions.Diagnostics.HealthChecks;
using XRaySystem.DeviceHost;

namespace XRaySystem.Tests;

public sealed class DeviceHostStorageReadinessCheckTests
{
    [Fact]
    public async Task Writable_acquisition_and_audit_directories_are_ready_without_leaving_probes()
    {
        var folder = CreateTempFolder();
        try
        {
            var acquisitionFolder = Path.Combine(folder, "acquisitions");
            var auditPath = Path.Combine(folder, "audit", "commands.jsonl");
            var check = new DeviceHostStorageReadinessCheck(
                new DeviceAcquisitionArchiveOptions(true, acquisitionFolder),
                new DeviceCommandAuditOptions(true, auditPath));

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Empty(Directory.GetFiles(folder, ".readiness-*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Audit_at_capacity_is_not_ready()
    {
        var folder = CreateTempFolder();
        try
        {
            var auditPath = Path.Combine(folder, "commands.jsonl");
            await File.WriteAllBytesAsync(auditPath, new byte[256]);
            var check = new DeviceHostStorageReadinessCheck(
                new DeviceAcquisitionArchiveOptions(false, Path.Combine(folder, "unused")),
                new DeviceCommandAuditOptions(
                    true,
                    auditPath,
                    MaximumFileBytes: 256));

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task File_where_acquisition_directory_is_expected_is_not_ready()
    {
        var folder = CreateTempFolder();
        try
        {
            var invalidDirectory = Path.Combine(folder, "not-a-directory");
            await File.WriteAllTextAsync(invalidDirectory, "occupied");
            var check = new DeviceHostStorageReadinessCheck(
                new DeviceAcquisitionArchiveOptions(true, invalidDirectory),
                new DeviceCommandAuditOptions(false, Path.Combine(folder, "unused.jsonl")));

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "xray-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
