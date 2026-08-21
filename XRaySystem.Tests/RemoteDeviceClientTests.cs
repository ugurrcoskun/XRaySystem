using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using XRaySystem.Contracts;
using XRaySystem.RemoteClient;

namespace XRaySystem.Tests;

public sealed class RemoteDeviceClientTests
{
    [Fact]
    public async Task Rest_client_uses_device_routes_and_generates_non_empty_command_ids()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));

            if (request.Method == HttpMethod.Get)
                return JsonResponse(CreateState());

            var commandId = JsonDocument.Parse(body!).RootElement.GetProperty("commandId").GetGuid();
            return JsonResponse(new DeviceCommandResult(commandId, true, "ok", CreateState()));
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088"), "mock-01"));

        var state = await client.GetStateAsync();
        var result = await client.UpdateParametersAsync(new AcquisitionParametersContract(80, 2400, 10));

        Assert.Equal("mock-01", state.DeviceId);
        Assert.True(result.Succeeded);
        Assert.NotEqual(Guid.Empty, result.CommandId);
        Assert.Equal("/api/devices/mock-01", requests[0].Path);
        Assert.Equal("/api/devices/mock-01/parameters", requests[1].Path);
        Assert.Contains("\"powerW\":2400", requests[1].Body);
        Assert.Contains("\"profile\":\"IndustrialQaPhantom\"", requests[1].Body);
    }

    [Fact]
    public async Task Binary_and_latest_live_downloads_validate_content_types_and_sequence()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("live/latest", StringComparison.Ordinal))
            {
                var response = BytesResponse([1, 2, 3], "image/bmp");
                response.Headers.Add("X-XRay-Sequence", "42");
                response.Headers.Add("X-XRay-Acquisition-Id", "c6dc720e-6707-4c78-a197-e38284d51564");
                response.Headers.Add("X-XRay-Width", "320");
                response.Headers.Add("X-XRay-Height", "240");
                return Task.FromResult(response);
            }
            return Task.FromResult(path.Contains("mode=raw", StringComparison.Ordinal)
                ? BytesResponse([4, 5], "application/vnd.xraysystem.gray16")
                : BytesResponse([6, 7], "image/bmp"));
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088/"), "mock-01"));
        var imageId = Guid.NewGuid();

        var low = await client.GetLowImageAsync(imageId);
        var raw = await client.GetRawImageAsync(imageId);
        var live = await client.GetLatestLiveFrameAsync(CreateTelemetry(sequence: 40));

        Assert.Equal([6, 7], low);
        Assert.Equal([4, 5], raw);
        Assert.NotNull(live);
        Assert.Equal(42, live.FrameSequence);
        Assert.Equal(Guid.Parse("c6dc720e-6707-4c78-a197-e38284d51564"), live.AcquisitionId);
        Assert.Equal(320, live.Width);
        Assert.Equal(240, live.Height);
        Assert.Equal(40, live.TriggerTelemetry.SequenceNumber);
        Assert.Equal([1, 2, 3], live.BmpBytes);
    }

    [Fact]
    public async Task Latest_live_returns_null_for_no_content()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088/"), "mock-01"));

        var frame = await client.GetLatestLiveFrameAsync(CreateTelemetry(sequence: 1));

        Assert.Null(frame);
    }

    private static DeviceStateContract CreateState() => new(
        "mock-01",
        "Connected",
        true,
        new AcquisitionParametersContract(70, 3500, 10),
        1,
        DateTimeOffset.UtcNow);

    private static TelemetryEventContract CreateTelemetry(long sequence) => new(
        "mock-01",
        sequence,
        "Capturing",
        Guid.NewGuid(),
        "LivePreview",
        512,
        512,
        true,
        512,
        512,
        DateTimeOffset.UtcNow,
        "IndustrialQaPhantom",
        new AcquisitionParametersContract(70, 3500, 10),
        new MeasuredTelemetryContract(70, 50, 3500, 1000, 10));

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage BytesResponse(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
