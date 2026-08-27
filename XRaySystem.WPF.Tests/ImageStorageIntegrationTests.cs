using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;
using XRaySystem.WPF.Services;

namespace XRaySystem.WPF.Tests;

public sealed class ImageStorageIntegrationTests
{
    [Fact]
    public void Gray16_tiff_round_trip_is_lossless_and_leaves_no_temp_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xray-tiff-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var storage = new ImageStorageService(directory);
            ushort[] sourcePixels = [0, 1, 32768, ushort.MaxValue];
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray16, null, sourcePixels, 4);
            source.Freeze();

            var path = storage.SaveTiff(source, "round-trip.tiff");
            var loaded = storage.LoadTiff(path);
            var loadedPixels = new ushort[4];
            loaded.CopyPixels(loadedPixels, 4, 0);

            Assert.True(File.Exists(path));
            Assert.Equal(PixelFormats.Gray16, loaded.Format);
            Assert.Equal(sourcePixels, loadedPixels);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Snapshot_uses_one_canonical_gray16_tiff_that_wpf_can_decode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xray-tiff-snapshot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var storage = new ImageStorageService(directory);
            ushort[] sourcePixels = [0, 1, 32768, ushort.MaxValue];
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray16, null, sourcePixels, 4);
            source.Freeze();
            var frame = new RawGray16Frame(
                sourcePixels,
                2,
                2,
                4,
                Gray16PixelFormat.Gray16LittleEndian,
                FrameKind.Snap,
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                new AcquisitionSettings(3, 70, 500, AcquisitionProfile.IndustrialQaPhantom),
                "wpf-integration-test",
                operation: AcquisitionOperation.Snap,
                snapFrameMode: SnapFrameMode.Raw);

            var files = storage.SaveSnapshot(source, frame, "snapshot");
            var decodedByWpf = storage.LoadTiff(files.PreviewPath);
            var wpfPixels = new ushort[4];
            decodedByWpf.CopyPixels(wpfPixels, 4, 0);
            var decodedBySourceCodec = storage.LoadTiff(files.RawPath);

            Assert.Equal(files.RawPath, files.PreviewPath);
            Assert.Equal(".tiff", Path.GetExtension(files.RawPath));
            Assert.Equal(PixelFormats.Gray16, decodedBySourceCodec.Format);
            Assert.Equal(sourcePixels, wpfPixels);
            using var stream = File.OpenRead(files.RawPath);
            Assert.Equal(sourcePixels, Gray16TiffCodec.Read(stream).Pixels);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.xrayraw"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
