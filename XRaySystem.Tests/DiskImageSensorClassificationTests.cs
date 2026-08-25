using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Sensors;

namespace XRaySystem.Tests;

public sealed class DiskImageSensorClassificationTests
{
    [Fact]
    public async Task Dicom_derived_disk_frame_is_always_classified_as_simulation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xray-disk-sensor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var bytes = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
            await File.WriteAllBytesAsync(Path.Combine(folder, "sample_snap.raw"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(folder, "sample_live.raw"), bytes);
            await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), """
                {
                  "version": 1,
                  "snap_width": 2,
                  "snap_height": 2,
                  "live_width": 2,
                  "live_height": 2,
                  "images": [
                    {
                      "entry_id": "test-entry",
                      "snap_file": "sample_snap.raw",
                      "live_file": "sample_live.raw",
                      "modality": "DX",
                      "source_file": "quarantined-test.dcm"
                    }
                  ]
                }
                """);

            using var sensor = new DiskImageSensorService(folder)
            {
                Parameters = new AcquisitionParameters(70, 3500, 1)
            };
            await sensor.ConnectAsync();
            var measurement = await sensor.SnapAsync(1);

            Assert.True(measurement.IsSimulator);
            Assert.Equal("DiskImageSensor:QuarantinedSample", measurement.SourceMarker);
            Assert.Contains("quarantined-test.dcm", sensor.CurrentSourceFile);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
