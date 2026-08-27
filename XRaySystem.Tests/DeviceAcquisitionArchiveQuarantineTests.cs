using Microsoft.Extensions.Logging.Abstractions;
using XRaySystem.Contracts;
using XRaySystem.DeviceHost;
using XRaySystem.Infrastructure.Sensors;

namespace XRaySystem.Tests;

public sealed class DeviceAcquisitionArchiveQuarantineTests
{
    [Fact]
    public void Startup_moves_stale_temporary_and_incomplete_artifacts_to_recoverable_quarantine()
    {
        var folder = CreateTempFolder();
        try
        {
            var staleRaw = Path.Combine(folder, "stale.tiff");
            var staleTemporary = Path.Combine(folder, "interrupted.bmp.transaction.tmp");
            File.WriteAllBytes(staleRaw, [0x01]);
            File.WriteAllBytes(staleTemporary, [0x02]);
            var staleTime = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(staleRaw, staleTime);
            File.SetLastWriteTimeUtc(staleTemporary, staleTime);

            _ = CreateArchive(folder, gracePeriodMinutes: 10);

            Assert.False(File.Exists(staleRaw));
            Assert.False(File.Exists(staleTemporary));
            var quarantine = Path.Combine(folder, "Quarantine");
            Assert.True(Directory.Exists(quarantine));
            Assert.Contains(
                Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path) == "stale.tiff");
            Assert.Contains(
                Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path) == "interrupted.bmp.transaction.tmp");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Startup_leaves_fresh_incomplete_artifacts_in_place_during_grace_period()
    {
        var folder = CreateTempFolder();
        try
        {
            var freshRaw = Path.Combine(folder, "fresh.tiff");
            var freshTemporary = Path.Combine(folder, "fresh.bmp.transaction.tmp");
            File.WriteAllBytes(freshRaw, [0x01]);
            File.WriteAllBytes(freshTemporary, [0x02]);

            _ = CreateArchive(folder, gracePeriodMinutes: 10);

            Assert.True(File.Exists(freshRaw));
            Assert.True(File.Exists(freshTemporary));
            Assert.False(Directory.Exists(Path.Combine(folder, "Quarantine")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Startup_does_not_quarantine_a_complete_artifact_set()
    {
        var folder = CreateTempFolder();
        try
        {
            var raw = Path.Combine(folder, "complete.xrayraw");
            var preview = Path.Combine(folder, "complete.bmp");
            var manifest = Path.Combine(folder, "complete.xraymeta.json");
            File.WriteAllBytes(raw, [0x01]);
            File.WriteAllBytes(preview, [(byte)'B', (byte)'M']);
            File.WriteAllText(manifest, "{}");
            var staleTime = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(raw, staleTime);
            File.SetLastWriteTimeUtc(preview, staleTime);
            File.SetLastWriteTimeUtc(manifest, staleTime);

            _ = CreateArchive(folder, gracePeriodMinutes: 10);

            Assert.True(File.Exists(raw));
            Assert.True(File.Exists(preview));
            Assert.True(File.Exists(manifest));
            Assert.False(Directory.Exists(Path.Combine(folder, "Quarantine")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Verification_failure_moves_the_whole_artifact_set_to_quarantine()
    {
        var folder = CreateTempFolder();
        try
        {
            var sensor = new MockXRaySensorService();
            var archive = CreateArchive(folder, gracePeriodMinutes: 10);
            using var coordinator = new DeviceCoordinator(
                sensor,
                new DeviceHostOptions("test-device", IsSimulator: true),
                new RemoteImageBuffer(new RemoteImageBufferOptions(
                    ImageLimit: 3,
                    MaxBytes: 32L * 1024 * 1024,
                    TimeToLive: TimeSpan.FromMinutes(1))),
                acquisitionArchive: archive);
            await coordinator.ConnectAsync(Command(), CancellationToken.None);
            await coordinator.SnapAsync(Command(), CancellationToken.None);
            var previewPath = Assert.Single(Directory.GetFiles(folder, "*.bmp"));
            var bytes = File.ReadAllBytes(previewPath);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(previewPath, bytes);

            Assert.Empty(archive.LoadRecent(10));
            Assert.Empty(Directory.GetFiles(folder, "*.tiff"));
            Assert.Empty(Directory.GetFiles(folder, "*.xrayraw"));
            Assert.Empty(Directory.GetFiles(folder, "*.bmp"));
            Assert.Empty(Directory.GetFiles(folder, "*.xraymeta.json"));
            var quarantined = Directory.GetFiles(
                Path.Combine(folder, "Quarantine"),
                "*",
                SearchOption.AllDirectories);
            Assert.Equal(3, quarantined.Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static DeviceAcquisitionArchive CreateArchive(string folder, double gracePeriodMinutes) =>
        new(
            new DeviceAcquisitionArchiveOptions(
                Enabled: true,
                BaseFolder: folder,
                QuarantineIncompleteArtifacts: true,
                IncompleteArtifactGracePeriodMinutes: gracePeriodMinutes),
            NullLogger<DeviceAcquisitionArchive>.Instance);

    private static DeviceCommandRequest Command() => new(Guid.NewGuid());

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "xray-device-quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
