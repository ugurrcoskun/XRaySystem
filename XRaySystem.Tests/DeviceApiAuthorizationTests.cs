using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using XRaySystem.Contracts;

namespace XRaySystem.Tests;

public sealed class DeviceApiAuthorizationTests : IDisposable
{
    private const string ViewerKey = "viewer-integration-key-0000000000000001";
    private const string OperatorKey = "operator-integration-key-00000000000001";
    private const string AdminKey = "admin-integration-key-0000000000000001";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

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
    public async Task Viewer_can_read_but_cannot_execute_commands()
    {
        using var readRequest = CreateGet("/api/devices/mock-01", ViewerKey);
        using var read = await _client.SendAsync(readRequest);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var command = CreateCommand("connect", ViewerKey, "viewer-client");
        using var forbidden = await _client.SendAsync(command);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
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

    private static HttpRequestMessage CreateGet(string route, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add(DeviceProtocolHeaders.ApiKey, apiKey);
        return request;
    }

    private static HttpRequestMessage CreateCommand(string operation, string apiKey, string clientId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/devices/mock-01/{operation}")
        {
            Content = JsonContent.Create(new DeviceCommandRequest(Guid.NewGuid()))
        };
        request.Headers.Add(DeviceProtocolHeaders.ApiKey, apiKey);
        request.Headers.Add(DeviceProtocolHeaders.ClientId, clientId);
        return request;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
