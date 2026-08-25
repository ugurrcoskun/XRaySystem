using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;

namespace XRaySystem.Tests;

public sealed class AcquisitionArtifactStorageTests
{
    [Fact]
    public void Save_writes_verified_raw_and_preview_without_temporary_orphans()
    {
        InTemporaryFolder(folder =>
        {
            var storage = new AcquisitionArtifactStorage(folder);
            var frame = Frame(Guid.NewGuid());

            var files = storage.Save(frame, Bmp(), ".bmp", "image_70.0kV_2.50mA_10ms_20260824_120000_000");

            Assert.True(File.Exists(files.RawPath));
            Assert.True(File.Exists(files.PreviewPath));
            var loaded = storage.LoadRaw(files.RawPath);
            Assert.Equal(frame.AcquisitionId, loaded.AcquisitionId);
            Assert.Equal(frame.Pixels, loaded.Pixels);
            Assert.Equal(Bmp(), storage.LoadPreview(files.PreviewPath));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.TopDirectoryOnly));
        });
    }

    [Fact]
    public void Collision_preserves_first_pair_and_uses_acquisition_id_suffix()
    {
        InTemporaryFolder(folder =>
        {
            var storage = new AcquisitionArtifactStorage(folder);
            const string stem = "image_70.0kV_2.50mA_10ms_20260824_120000_000";
            var firstFrame = Frame(Guid.NewGuid());
            var secondFrame = Frame(Guid.NewGuid());

            var first = storage.Save(firstFrame, Bmp(1), ".bmp", stem);
            var second = storage.Save(secondFrame, Bmp(2), ".bmp", stem);

            Assert.Equal(stem, first.FileStem);
            Assert.Equal($"{stem}_{secondFrame.AcquisitionId:N}", second.FileStem);
            Assert.Equal(firstFrame.AcquisitionId, storage.LoadRaw(first.RawPath).AcquisitionId);
            Assert.Equal(secondFrame.AcquisitionId, storage.LoadRaw(second.RawPath).AcquisitionId);
            Assert.NotEqual(storage.LoadPreview(first.PreviewPath), storage.LoadPreview(second.PreviewPath));
        });
    }

    [Fact]
    public void Invalid_preview_is_rejected_before_any_file_is_created()
    {
        InTemporaryFolder(folder =>
        {
            var storage = new AcquisitionArtifactStorage(folder);

            Assert.Throws<InvalidDataException>(() =>
                storage.Save(Frame(Guid.NewGuid()), [1, 2, 3, 4], ".bmp", "image_invalid"));

            Assert.Empty(Directory.GetFiles(folder));
        });
    }

    [Fact]
    public void Load_rejects_paths_outside_the_configured_storage_root()
    {
        InTemporaryFolder(folder =>
        {
            var storage = new AcquisitionArtifactStorage(folder);
            var outside = Path.Combine(Path.GetDirectoryName(folder)!, "outside-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(outside, Bmp());
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => storage.LoadPreview(outside));
            }
            finally
            {
                File.Delete(outside);
            }
        });
    }

    [Fact]
    public void SaveWithMetadata_commits_raw_preview_and_json_with_metadata_as_commit_marker()
    {
        InTemporaryFolder(folder =>
        {
            var storage = new AcquisitionArtifactStorage(folder);
            var metadata = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");

            var files = storage.SaveWithMetadata(
                Frame(Guid.NewGuid()),
                Bmp(),
                ".bmp",
                "image_with_metadata",
                metadata);

            Assert.True(File.Exists(files.RawPath));
            Assert.True(File.Exists(files.PreviewPath));
            Assert.NotNull(files.MetadataPath);
            Assert.True(File.Exists(files.MetadataPath));
            Assert.Equal(metadata, storage.LoadMetadata(files.MetadataPath!));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        });
    }

    [Fact]
    public void SaveWithMetadata_rejects_invalid_json_before_creating_artifacts()
    {
        InTemporaryFolder(folder =>
        {
            var storage = new AcquisitionArtifactStorage(folder);

            Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
                storage.SaveWithMetadata(
                    Frame(Guid.NewGuid()),
                    Bmp(),
                    ".bmp",
                    "image_invalid_metadata",
                    [1, 2, 3]));

            Assert.Empty(Directory.GetFiles(folder));
        });
    }

    [Theory]
    [InlineData((int)AcquisitionArtifactWriteStage.PreviewTempFlushed)]
    [InlineData((int)AcquisitionArtifactWriteStage.MetadataTempFlushed)]
    [InlineData((int)AcquisitionArtifactWriteStage.RawTempVerified)]
    [InlineData((int)AcquisitionArtifactWriteStage.RawPromoted)]
    [InlineData((int)AcquisitionArtifactWriteStage.PreviewPromoted)]
    [InlineData((int)AcquisitionArtifactWriteStage.MetadataPromoted)]
    public void Injected_io_failure_at_each_commit_stage_leaves_no_visible_or_temporary_artifacts(
        int failureStageValue)
    {
        InTemporaryFolder(folder =>
        {
            var failureStage = (AcquisitionArtifactWriteStage)failureStageValue;
            var storage = new AcquisitionArtifactStorage(folder, stage =>
            {
                if (stage == failureStage) throw new IOException("Injected disk failure.");
            });

            Assert.Throws<IOException>(() =>
                storage.SaveWithMetadata(
                    Frame(Guid.NewGuid()),
                    Bmp(),
                    ".bmp",
                    "image_fault_injection",
                    System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1}")));

            Assert.Empty(Directory.GetFiles(folder));
        });
    }

    private static RawGray16Frame Frame(Guid acquisitionId) => new(
        [1, 2, 3, 4],
        2,
        2,
        4,
        Gray16PixelFormat.Gray16LittleEndian,
        FrameKind.Snap,
        new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
        acquisitionId,
        new AcquisitionSettings(2.5, 70, 10, AcquisitionProfile.IndustrialQaPhantom),
        "artifact-test",
        operation: AcquisitionOperation.Snap,
        snapFrameMode: SnapFrameMode.Raw);

    private static byte[] Bmp(byte marker = 0) => [(byte)'B', (byte)'M', marker, 0, 0, 0];

    private static void InTemporaryFolder(Action<string> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), "xray-artifact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { test(folder); }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
