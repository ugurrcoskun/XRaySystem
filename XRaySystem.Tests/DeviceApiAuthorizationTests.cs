using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XRaySystem.Contracts;
using XRaySystem.DeviceHost;
using XRaySystem.Infrastructure.Storage;

namespace XRaySystem.Tests;

public sealed class DeviceApiAuthorizationTests : IDisposable
{
    private const string ViewerKey = "viewer-integration-key-0000000000000001";
    private const string OperatorKey = "operator-integration-key-00000000000001";
    private const string AdminKey = "admin-integration-key-0000000000000001";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _archiveFolder = Path.Combine(
        Path.GetTempPath(),
        "xray-device-api-archive-" + Guid.NewGuid().ToString("N"));
    private readonly string _auditFilePath = Path.Combine(
        Path.GetTempPath(),
        "xray-device-api-audit-" + Guid.NewGuid().ToString("N"),
        "device-audit.jsonl");

    public DeviceApiAuthorizationTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RequireHttps"] = "false",
                    ["Security:AuthenticationEnabled"] = "true",
                    ["AcquisitionStorage:Enabled"] = "true",
                    ["AcquisitionStorage:BaseFolder"] = _archiveFolder,
                    ["AcquisitionStorage:RequireSignedMetadata"] = "true",
                    ["AcquisitionStorage:IntegrityKey"] = "integration-test-archive-integrity-key-0001",
                    ["AuditStorage:Enabled"] = "true",
                    ["AuditStorage:FilePath"] = _auditFilePath,
                    ["AuditStorage:RequireSignedEntries"] = "true",
                    ["AuditStorage:IntegrityKey"] = "integration-test-audit-integrity-key-000001",
                    ["Security:ApiKeys:0:Key"] = ViewerKey,
                    ["Security:ApiKeys:0:Subject"] = "viewer-test",
                    ["Security:ApiKeys:0:Role"] = "Viewer",
                    ["Security:ApiKeys:1:Key"] = OperatorKey,
                    ["Security:ApiKeys:1:Subject"] = "operator-test",
                    ["Security:ApiKeys:1:Role"] = "Operator",
                    ["Security:ApiKeys:2:Key"] = AdminKey,
                    ["Security:ApiKeys:2:Subject"] = "admin-test",
                    ["Security:ApiKeys:2:Role"] = "Admin"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DeviceAcquisitionArchiveOptions>();
                services.RemoveAll<IDeviceAcquisitionArchive>();
                services.RemoveAll<DeviceCommandAuditOptions>();
                services.RemoveAll<IDeviceCommandAuditStore>();
                services.AddSingleton(new DeviceAcquisitionArchiveOptions(
                    true,
                    _archiveFolder,
                    RequireSignedMetadata: true,
                    IntegrityKey: "integration-test-archive-integrity-key-0001"));
                services.AddSingleton<IDeviceAcquisitionArchive, DeviceAcquisitionArchive>();
                services.AddSingleton(new DeviceCommandAuditOptions(
                    true,
                    _auditFilePath,
                    RequireSignedEntries: true,
                    IntegrityKey: "integration-test-audit-integrity-key-000001"));
                services.AddSingleton<IDeviceCommandAuditStore, HashChainedJsonlDeviceAuditStore>();
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Protected_endpoint_rejects_missing_or_invalid_key()
    {
        using var missing = await _client.GetAsync("/api/devices/mock-01");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var invalidRequest = CreateGet("/api/devices/mock-01", "invalid-key-value");
        using var invalid = await _client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task Liveness_and_durable_storage_readiness_are_separate_and_healthy()
    {
        using var liveness = await _client.GetAsync("/healthz");
        using var readiness = await _client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
    }

    [Fact]
    public async Task Bearer_api_key_authenticates_mobile_requests()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/devices/mock-01");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ViewerKey);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sending_header_and_bearer_credentials_together_is_rejected()
    {
        using var request = CreateGet("/api/devices/mock-01", ViewerKey);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ViewerKey);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authentication_logs_do_not_contain_presented_or_configured_api_keys()
    {
        var logs = new CapturingLoggerProvider();
        using var loggingFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = loggingFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });
        const string invalidPresentedKey = "invalid-log-leak-sentinel-key-00000001";
        using var invalidRequest = CreateGet("/api/devices/mock-01", invalidPresentedKey);
        using var invalid = await client.SendAsync(invalidRequest);
        using var validRequest = CreateGet("/api/devices/mock-01", ViewerKey);
        using var valid = await client.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var combined = string.Join('\n', logs.Messages);
        Assert.DoesNotContain(invalidPresentedKey, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(ViewerKey, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(OperatorKey, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminKey, combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_can_read_but_cannot_execute_commands()
    {
        using var readRequest = CreateGet("/api/devices/mock-01", ViewerKey);
        using var read = await _client.SendAsync(readRequest);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var command = CreateCommand("connect", ViewerKey, "viewer-client");
        using var forbidden = await _client.SendAsync(command);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_protocol_capabilities()
    {
        using var request = CreateGet(DeviceApiRoutes.Protocol, ViewerKey);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var protocol = await response.Content.ReadFromJsonAsync<DeviceProtocolContract>();
        Assert.NotNull(protocol);
        Assert.Equal(DeviceProtocolVersion.Current, protocol.ProtocolVersion);
        Assert.Contains("Grab", protocol.AcquisitionOperations);
        Assert.Contains("Low", protocol.SnapFrameModes);
    }

    [Theory]
    [InlineData(OperatorKey, "operator-client")]
    [InlineData(AdminKey, "admin-client")]
    public async Task Operator_and_admin_can_execute_commands(string apiKey, string clientId)
    {
        using var connectRequest = CreateCommand("connect", apiKey, clientId);
        using var connect = await _client.SendAsync(connectRequest);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

        using var disconnectRequest = CreateCommand("disconnect", apiKey, clientId);
        using var disconnect = await _client.SendAsync(disconnectRequest);
        Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);
    }

    [Fact]
    public async Task Operator_can_use_grab_freeze_abort_and_snap_low_routes()
    {
        const string clientId = "operator-grab-client";
        using var connectRequest = CreateCommand("connect", OperatorKey, clientId);
        using var connect = await _client.SendAsync(connectRequest);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

        using var grabRequest = CreateCommand("grab/start", OperatorKey, clientId);
        using var grab = await _client.SendAsync(grabRequest);
        Assert.Equal(HttpStatusCode.OK, grab.StatusCode);

        using var freezeRequest = CreateCommand("grab/freeze", OperatorKey, clientId);
        using var freeze = await _client.SendAsync(freezeRequest);
        Assert.Equal(HttpStatusCode.OK, freeze.StatusCode);

        using var snapRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/devices/mock-01/snap")
        {
            Content = JsonContent.Create(
                new SnapCommandRequest(Guid.NewGuid(), SnapFrameModeContract.Low))
        };
        snapRequest.Headers.Add(DeviceProtocolHeaders.ApiKey, OperatorKey);
        snapRequest.Headers.Add(DeviceProtocolHeaders.ClientId, clientId);
        using var snap = await _client.SendAsync(snapRequest);
        Assert.Equal(HttpStatusCode.OK, snap.StatusCode);
        var result = await snap.Content.ReadFromJsonAsync<SnapCommandResult>();
        Assert.NotNull(result);
        Assert.Equal(SnapFrameModeContract.Low, result.Image.SnapFrameMode);
        Assert.Equal(512, result.Image.Width);
        Assert.Equal(512, result.Image.Height);
        Assert.True(result.Image.PersistedOnDevice);
        var rawPath = Assert.Single(Directory.GetFiles(_archiveFolder, "*.xrayraw"));
        Assert.Single(Directory.GetFiles(_archiveFolder, "*.bmp"));
        Assert.Single(Directory.GetFiles(_archiveFolder, "*.xraymeta.json"));
        Assert.Contains(
            "integrityHmacSha256",
            File.ReadAllText(Assert.Single(Directory.GetFiles(_archiveFolder, "*.xraymeta.json"))),
            StringComparison.Ordinal);
        using var rawStream = File.OpenRead(rawPath);
        Assert.Equal(result.Image.AcquisitionId, Gray16RawFileCodec.Read(rawStream).AcquisitionId);
        var auditLines = File.ReadAllLines(_auditFilePath);
        Assert.True(auditLines.Length >= 8);
        Assert.Contains(auditLines, line => line.Contains("integrityHmacSha256", StringComparison.Ordinal));
        Assert.DoesNotContain(auditLines, line => line.Contains(OperatorKey, StringComparison.Ordinal));

        using var abortRequest = CreateCommand("abort", OperatorKey, clientId);
        using var abort = await _client.SendAsync(abortRequest);
        Assert.Equal(HttpStatusCode.OK, abort.StatusCode);
    }

    [Fact]
    public async Task Persistence_failure_returns_507_and_same_command_retry_stays_failed()
    {
        using var failureFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeviceAcquisitionArchive>();
                services.AddSingleton<IDeviceAcquisitionArchive>(new FailingArchive());
            });
        });
        using var client = failureFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });
        const string clientId = "operator-persistence-failure";
        using var connectRequest = CreateCommand("connect", OperatorKey, clientId);
        using var connect = await client.SendAsync(connectRequest);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
        var commandId = Guid.NewGuid();

        using var firstRequest = CreateSnap(commandId, OperatorKey, clientId);
        using var first = await client.SendAsync(firstRequest);
        using var retryRequest = CreateSnap(commandId, OperatorKey, clientId);
        using var retry = await client.SendAsync(retryRequest);

        Assert.Equal((HttpStatusCode)507, first.StatusCode);
        Assert.Equal((HttpStatusCode)507, retry.StatusCode);
    }

    [Fact]
    public async Task Unavailable_durable_audit_rejects_command_before_device_execution()
    {
        using var failureFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeviceCommandAuditStore>();
                services.AddSingleton<IDeviceCommandAuditStore>(new FailingAuditStore());
            });
        });
        using var client = failureFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });
        using var request = CreateCommand("connect", OperatorKey, "audit-failure-client");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            nameof(XRaySystem.Domain.Entities.DeviceStatus.Disconnected),
            failureFactory.Services.GetRequiredService<DeviceCoordinator>().GetState().Status);
    }

    [Fact]
    public async Task Command_seen_before_host_restart_is_rejected_without_device_execution()
    {
        var previousCommandId = Guid.NewGuid();
        var replayAudit = new ReplayAuditStore(previousCommandId);
        using var replayFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeviceCommandAuditStore>();
                services.AddSingleton<IDeviceCommandAuditStore>(replayAudit);
            });
        });
        using var client = replayFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });
        using var request = CreateCommand(
            "connect",
            OperatorKey,
            "restart-replay-client",
            previousCommandId);

        using var response = await client.SendAsync(request);
        using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("command_replay_after_restart", problem.RootElement.GetProperty("code").GetString());
        Assert.Contains(replayAudit.Events, item =>
            item.CommandId == previousCommandId && item.Outcome == "replay_rejected");
        Assert.Equal(
            nameof(XRaySystem.Domain.Entities.DeviceStatus.Disconnected),
            replayFactory.Services.GetRequiredService<DeviceCoordinator>().GetState().Status);
    }

    [Fact]
    public async Task SignalR_negotiate_requires_viewer_or_higher()
    {
        using var missing = await _client.PostAsync(
            "/hubs/device-events/negotiate?negotiateVersion=1",
            content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/hubs/device-events/negotiate?negotiateVersion=1");
        request.Headers.Add(DeviceProtocolHeaders.ApiKey, ViewerKey);
        using var allowed = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Api_rejects_oversized_request_body_before_command_execution()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/mock-01/connect")
        {
            Content = new ByteArrayContent(new byte[65_537])
        };
        request.Headers.Add(DeviceProtocolHeaders.ApiKey, OperatorKey);
        request.Headers.Add(DeviceProtocolHeaders.ClientId, "oversized-body-client");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_json_is_rejected_before_command_execution()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/mock-01/connect")
        {
            Content = new StringContent("{\"commandId\":", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(DeviceProtocolHeaders.ApiKey, OperatorKey);
        request.Headers.Add(DeviceProtocolHeaders.ClientId, "malformed-json-client");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(XRaySystem.Domain.Entities.DeviceStatus.Disconnected),
            _factory.Services.GetRequiredService<DeviceCoordinator>().GetState().Status);
    }

    [Fact]
    public async Task Empty_command_id_is_rejected_without_device_execution()
    {
        using var request = CreateCommand(
            "connect",
            OperatorKey,
            "empty-command-client",
            Guid.Empty);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(XRaySystem.Domain.Entities.DeviceStatus.Disconnected),
            _factory.Services.GetRequiredService<DeviceCoordinator>().GetState().Status);
    }

    [Fact]
    public async Task Api_rate_limits_repeated_authenticated_requests_and_adds_no_store_headers()
    {
        HttpResponseMessage? last = null;
        try
        {
            for (var index = 0; index < 121; index++)
            {
                last?.Dispose();
                using var request = CreateGet(DeviceApiRoutes.Protocol, ViewerKey);
                last = await _client.SendAsync(request);
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
            Assert.Contains("no-store", last.Headers.CacheControl?.ToString() ?? string.Empty);
            Assert.True(last.Headers.TryGetValues("X-Content-Type-Options", out var values));
            Assert.Contains("nosniff", values);
        }
        finally
        {
            last?.Dispose();
        }
    }

    private static HttpRequestMessage CreateGet(string route, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add(DeviceProtocolHeaders.ApiKey, apiKey);
        return request;
    }

    private static HttpRequestMessage CreateCommand(
        string operation,
        string apiKey,
        string clientId,
        Guid? commandId = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/devices/mock-01/{operation}")
        {
            Content = JsonContent.Create(new DeviceCommandRequest(commandId ?? Guid.NewGuid()))
        };
        request.Headers.Add(DeviceProtocolHeaders.ApiKey, apiKey);
        request.Headers.Add(DeviceProtocolHeaders.ClientId, clientId);
        return request;
    }

    private static HttpRequestMessage CreateSnap(Guid commandId, string apiKey, string clientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/mock-01/snap")
        {
            Content = JsonContent.Create(new SnapCommandRequest(commandId, SnapFrameModeContract.Raw))
        };
        request.Headers.Add(DeviceProtocolHeaders.ApiKey, apiKey);
        request.Headers.Add(DeviceProtocolHeaders.ClientId, clientId);
        return request;
    }

    private sealed class FailingArchive : IDeviceAcquisitionArchive
    {
        public DeviceAcquisitionArchiveReceipt Save(
            XRaySystem.Domain.Entities.XRayMeasurement measurement,
            RemoteImageMetadataContract metadata,
            byte[] lowPreviewBmp) =>
            throw new AcquisitionPersistenceException(
                "Test persistence failure.",
                new IOException("Disk unavailable."));

        public IReadOnlyList<ArchivedRemoteImage> LoadRecent(int maximumCount) => [];
    }

    private sealed class FailingAuditStore : IDeviceCommandAuditStore
    {
        public bool WasCommandObservedBeforeStartup(Guid commandId) => false;

        public Task AppendAsync(
            DeviceCommandAuditEvent auditEvent,
            CancellationToken cancellationToken = default) =>
            throw new AuditPersistenceException(
                "Test audit failure.",
                new IOException("Audit disk unavailable."));
    }

    private sealed class ReplayAuditStore(Guid observedCommandId) : IDeviceCommandAuditStore
    {
        public List<DeviceCommandAuditEvent> Events { get; } = [];

        public bool WasCommandObservedBeforeStartup(Guid commandId) => commandId == observedCommandId;

        public Task AppendAsync(
            DeviceCommandAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
        public void Dispose() { }

        private sealed class CapturingLogger(
            System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null) messages.Enqueue(exception.ToString());
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_archiveFolder)) Directory.Delete(_archiveFolder, recursive: true);
        var auditFolder = Path.GetDirectoryName(_auditFilePath)!;
        if (Directory.Exists(auditFolder)) Directory.Delete(auditFolder, recursive: true);
    }
}
