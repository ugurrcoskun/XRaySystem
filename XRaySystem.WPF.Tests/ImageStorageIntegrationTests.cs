using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
}
