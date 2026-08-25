using XRaySystem.DeviceHost;

namespace XRaySystem.Tests;

public sealed class DeviceCommandAuditStoreTests
{
    private const string IntegrityKey = "device-audit-store-integrity-test-key-00001";

    [Fact]
    public async Task Signed_hash_chain_survives_restart_and_appends_monotonic_entries()
    {
        await InTemporaryAuditAsync(async (path, options) =>
        {
            var firstStore = new HashChainedJsonlDeviceAuditStore(options);
            await firstStore.AppendAsync(Event(Guid.NewGuid(), "started"));
            await firstStore.AppendAsync(Event(Guid.NewGuid(), "succeeded"));

            var restartedStore = new HashChainedJsonlDeviceAuditStore(options);
            await restartedStore.AppendAsync(Event(Guid.NewGuid(), "rejected"));

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(3, lines.Length);
            Assert.Contains("\"sequence\":1", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"sequence\":3", lines[2], StringComparison.Ordinal);
            Assert.All(lines, line => Assert.Contains("integrityHmacSha256", line, StringComparison.Ordinal));
            Assert.DoesNotContain(IntegrityKey, string.Join('\n', lines), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Restart_remembers_previously_observed_command_ids_without_marking_current_process_appends()
    {
        await InTemporaryAuditAsync(async (_, options) =>
        {
            var previousCommandId = Guid.NewGuid();
            var currentStore = new HashChainedJsonlDeviceAuditStore(options);
            Assert.False(currentStore.WasCommandObservedBeforeStartup(previousCommandId));
            await currentStore.AppendAsync(Event(previousCommandId, "started"));
            Assert.False(currentStore.WasCommandObservedBeforeStartup(previousCommandId));

            var restartedStore = new HashChainedJsonlDeviceAuditStore(options);

            Assert.True(restartedStore.WasCommandObservedBeforeStartup(previousCommandId));
            Assert.False(restartedStore.WasCommandObservedBeforeStartup(Guid.NewGuid()));
        });
    }

    [Fact]
    public async Task Tampered_audit_line_prevents_store_restart()
    {
        await InTemporaryAuditAsync(async (path, options) =>
        {
            var store = new HashChainedJsonlDeviceAuditStore(options);
            await store.AppendAsync(Event(Guid.NewGuid(), "started"));
            var json = await File.ReadAllTextAsync(path);
            var tampered = json.Replace("audit-test-user", "audit-evil-user");
            Assert.NotEqual(json, tampered);
            await File.WriteAllTextAsync(path, tampered);

            Assert.Throws<InvalidDataException>(() => new HashChainedJsonlDeviceAuditStore(options));
        });
    }

    [Fact]
    public void Signed_audit_requires_a_separate_strong_key()
    {
        var options = new DeviceCommandAuditOptions(
            true,
            Path.Combine(Path.GetTempPath(), "unused-audit.jsonl"),
            RequireSignedEntries: true);

        Assert.Throws<ArgumentException>(options.EnsureValid);
        Assert.Throws<ArgumentException>((options with { IntegrityKey = "weak" }).EnsureValid);
    }

    [Fact]
    public async Task Audit_capacity_limit_fails_closed_without_corrupting_the_existing_chain()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xray-audit-capacity-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "audit.jsonl");
        try
        {
            var options = new DeviceCommandAuditOptions(
                true,
                path,
                RequireSignedEntries: true,
                IntegrityKey: IntegrityKey);
            var store = new HashChainedJsonlDeviceAuditStore(options);
            await store.AppendAsync(Event(Guid.NewGuid(), "started"));
            var committedLength = new FileInfo(path).Length;
            var bounded = new HashChainedJsonlDeviceAuditStore(
                options with { MaximumFileBytes = committedLength });

            await Assert.ThrowsAsync<AuditPersistenceException>(() =>
                bounded.AppendAsync(Event(Guid.NewGuid(), "started")));

            Assert.Equal(committedLength, new FileInfo(path).Length);
            _ = new HashChainedJsonlDeviceAuditStore(options);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static DeviceCommandAuditEvent Event(Guid commandId, string outcome) => new(
        DateTimeOffset.UtcNow,
        "mock-01",
        "audit-test-user",
        "Operator",
        "audit-test-client",
        commandId,
        "snap",
        outcome,
        outcome == "started" ? null : 200,
        null);

    private static async Task InTemporaryAuditAsync(
        Func<string, DeviceCommandAuditOptions, Task> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), "xray-audit-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "device-audit.jsonl");
        var options = new DeviceCommandAuditOptions(
            true,
            path,
            RequireSignedEntries: true,
            IntegrityKey: IntegrityKey);
        try
        {
            await test(path, options);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
