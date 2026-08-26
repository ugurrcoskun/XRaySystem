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
    public async Task Single_image_metadata_query_uses_the_targeted_catalog_route()
    {
        var imageId = Guid.NewGuid();
        var acquisitionId = Guid.NewGuid();
        var metadata = new RemoteImageMetadataContract(
            imageId,
            acquisitionId,
            "Snap",
            2,
            2,
            4,
            "Gray16LittleEndian",
            2,
            2,
            DateTimeOffset.UtcNow,
            "IndustrialQaPhantom",
            new AcquisitionParametersContract(70, 210, 10),
            new MeasuredTelemetryContract(70, 3, 210, 1, 10),
            true,
            true);
        string? requestedPath = null;
        var handler = new StubHandler((request, _) =>
        {
            requestedPath = request.RequestUri!.PathAndQuery;
            return Task.FromResult(JsonResponse(metadata));
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088"), "mock-01"));

        var result = await client.GetImageAsync(imageId);

        Assert.Equal(imageId, result.ImageId);
        Assert.Equal($"/api/devices/mock-01/images/{imageId:D}", requestedPath);
    }

    [Fact]
    public async Task Image_catalog_uses_cursor_pages_without_offset_gaps()
    {
        var first = CreateImageMetadata(DateTimeOffset.UtcNow);
        var second = CreateImageMetadata(first.CapturedAtUtc.AddSeconds(-1));
        var requestedPaths = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            requestedPaths.Add(request.RequestUri!.PathAndQuery);
            var hasCursor = request.RequestUri.Query.Contains("cursor=next-page", StringComparison.Ordinal);
            var page = hasCursor
                ? new RemoteImageCatalogPageContract([second], null)
                : new RemoteImageCatalogPageContract([first], "next-page");
            return Task.FromResult(JsonResponse(page));
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088"), "mock-01"));

        var images = await client.GetImagesAsync(2);

        Assert.Equal([first.ImageId, second.ImageId], images.Select(item => item.ImageId));
        Assert.Equal(2, requestedPaths.Count);
        Assert.Equal("/api/devices/mock-01/images/page?take=2", requestedPaths[0]);
        Assert.Contains("cursor=next-page", requestedPaths[1]);
    }

    [Fact]
    public async Task Grab_freeze_abort_and_snap_mode_use_the_new_protocol_routes()
    {
        var requests = new List<(string Path, string Body)>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            requests.Add((request.RequestUri!.AbsolutePath, body));
            var commandId = JsonDocument.Parse(body).RootElement.GetProperty("commandId").GetGuid();
            if (request.RequestUri.AbsolutePath.EndsWith("/snap", StringComparison.Ordinal))
            {
                var image = new RemoteImageMetadataContract(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Snap",
                    2,
                    2,
                    4,
                    "Gray16LittleEndian",
                    2,
                    2,
                    DateTimeOffset.UtcNow,
                    "IndustrialQaPhantom",
                    new AcquisitionParametersContract(70, 3500, 10),
                    new MeasuredTelemetryContract(70, 50, 3500, 1, 10),
                    true,
                    true,
                    AcquisitionOperationContract.Snap,
                    SnapFrameModeContract.Low);
                return JsonResponse(new SnapCommandResult(commandId, true, "ok", CreateState(), image));
            }

            return JsonResponse(new DeviceCommandResult(commandId, true, "ok", CreateState()));
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088"), "mock-01"));

        await client.StartGrabAsync();
        await client.FreezeGrabAsync();
        await client.AbortAsync();
        var snap = await client.SnapAsync(SnapFrameModeContract.Low);

        Assert.Equal(
            [
                "/api/devices/mock-01/grab/start",
                "/api/devices/mock-01/grab/freeze",
                "/api/devices/mock-01/abort",
                "/api/devices/mock-01/snap"
            ],
            requests.Select(item => item.Path));
        Assert.Contains("\"frameMode\":\"Low\"", requests[^1].Body);
        Assert.Equal(SnapFrameModeContract.Low, snap.Image.SnapFrameMode);
    }

    [Fact]
    public async Task Dynamic_mobile_credential_is_added_as_bearer_without_persisting_on_http_client()
    {
        const string key = "mobile-test-key-with-at-least-32-characters";
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(key, request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(CreateState()));
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(
                new Uri("https://device-host.local:7088"),
                "mock-01",
                ApiKeyProvider: _ => Task.FromResult<string?>(key)));

        var state = await client.GetStateAsync();

        Assert.Equal("mock-01", state.DeviceId);
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Protocol_validation_accepts_required_capabilities_and_rejects_wrong_version()
    {
        var version = DeviceProtocolVersion.Current;
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            new DeviceProtocolContract(
                version,
                "XRaySystem.DeviceHost",
                "mock-01",
                true,
                ["Snap", "Grab", "Freeze", "Abort"],
                ["Raw", "Low"]))));
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088"), "mock-01"));

        await client.ValidateProtocolAsync();
        version = DeviceProtocolVersion.Current + 1;

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ValidateProtocolAsync());
    }

    [Fact]
    public async Task Binary_and_latest_live_downloads_validate_content_types_and_sequence()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("grab/latest", StringComparison.Ordinal))
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

    [Fact]
    public async Task Problem_details_are_exposed_as_bounded_structured_remote_errors()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)507)
        {
            Content = JsonContent.Create(new
            {
                title = "Storage failure",
                status = 507,
                detail = "Görüntü cihaz bilgisayarında kalıcı olarak doğrulanamadı.",
                error = "persistence_failed"
            })
        }));
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088/"), "mock-01"));

        var error = await Assert.ThrowsAsync<RemoteDeviceException>(() =>
            client.ConnectDeviceAsync());

        Assert.Equal((HttpStatusCode)507, error.StatusCode);
        Assert.Equal("persistence_failed", error.ErrorCode);
        Assert.Equal("Görüntü cihaz bilgisayarında kalıcı olarak doğrulanamadı.", error.Message);
    }

    [Fact]
    public async Task Non_json_proxy_errors_use_safe_status_fallback_without_echoing_body()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("proxy-internal-secret-details")
        }));
        using var httpClient = new HttpClient(handler);
        await using var client = new RemoteDeviceClient(
            httpClient,
            new RemoteDeviceClientOptions(new Uri("http://localhost:5088/"), "mock-01"));

        var error = await Assert.ThrowsAsync<RemoteDeviceException>(() => client.GetStateAsync());

        Assert.Equal("remote_request_failed", error.ErrorCode);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceStateContract CreateState() => new(
        "mock-01",
        "Connected",
        true,
        new AcquisitionParametersContract(70, 3500, 10),
        1,
        DateTimeOffset.UtcNow);

    private static RemoteImageMetadataContract CreateImageMetadata(DateTimeOffset capturedAtUtc) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Snap",
        2,
        2,
        4,
        "Gray16LittleEndian",
        2,
        2,
        capturedAtUtc,
        "IndustrialQaPhantom",
        new AcquisitionParametersContract(70, 210, 10),
        new MeasuredTelemetryContract(70, 3, 210, 1, 10),
        true,
        true);

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
