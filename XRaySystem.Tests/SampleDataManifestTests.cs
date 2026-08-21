using System.Text.Json;
using System.Xml.Linq;

namespace XRaySystem.Tests;

public sealed class SampleDataManifestTests
{
    [Fact]
    public void Unverified_sample_data_is_quarantined_and_not_packaged_by_default()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sampleData = Path.Combine(repositoryRoot, "XRaySystem.Infrastructure", "SampleData");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(sampleData, "manifest.json")));
        var distribution = manifest.RootElement.GetProperty("distribution");

        Assert.Equal("quarantined", distribution.GetProperty("status").GetString());
        Assert.False(distribution.GetProperty("approved").GetBoolean());
        Assert.False(distribution.GetProperty("source_license_verified").GetBoolean());
        Assert.False(distribution.GetProperty("metadata_anonymized").GetBoolean());
        Assert.False(distribution.GetProperty("pixel_annotations_reviewed").GetBoolean());

        var projectPath = Path.Combine(repositoryRoot, "XRaySystem.WPF", "XRaySystem.WPF.csproj");
        var project = XDocument.Load(projectPath);
        var sampleIncludes = project.Descendants("Content")
            .Select(element => new
            {
                Include = element.Attribute("Include")?.Value ?? string.Empty,
                GroupCondition = element.Parent?.Attribute("Condition")?.Value ?? string.Empty
            })
            .Where(item => item.Include.Contains("SampleData", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(sampleIncludes);
        Assert.All(sampleIncludes, item =>
        {
            Assert.DoesNotContain("**", item.Include, StringComparison.Ordinal);
            Assert.DoesNotContain(".dcm", item.Include, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IncludeUnverifiedSampleData", item.GroupCondition, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Manifest_references_every_raw_file_once_with_the_expected_size()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sampleData = Path.Combine(repositoryRoot, "XRaySystem.Infrastructure", "SampleData");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(sampleData, "manifest.json")));

        var referencedFiles = new List<string>();
        foreach (var image in manifest.RootElement.GetProperty("images").EnumerateArray())
        {
            ValidateFile(image, "snap_file", "snap_width", "snap_height");
            ValidateFile(image, "live_file", "live_width", "live_height");
        }

        var rawFiles = Directory.GetFiles(sampleData, "*.raw")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var orderedReferences = referencedFiles.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(orderedReferences.Distinct(StringComparer.Ordinal).Count(), orderedReferences.Length);
        Assert.Equal(rawFiles, orderedReferences);

        void ValidateFile(JsonElement image, string fileProperty, string widthProperty, string heightProperty)
        {
            var filename = image.GetProperty(fileProperty).GetString();
            Assert.False(string.IsNullOrWhiteSpace(filename));
            referencedFiles.Add(filename!);

            var path = Path.Combine(sampleData, filename!);
            Assert.True(File.Exists(path), $"Manifest dosyası bulunamadı: {filename}");
            var width = image.GetProperty(widthProperty).GetInt32();
            var height = image.GetProperty(heightProperty).GetInt32();
            Assert.Equal(checked((long)width * height * sizeof(ushort)), new FileInfo(path).Length);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRaySystem.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("XRaySystem.slnx üst dizinlerde bulunamadı.");
    }
}
