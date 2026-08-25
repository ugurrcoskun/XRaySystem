using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using XRaySystem.Contracts;

namespace XRaySystem.RemoteClient;

public sealed record RemoteLowFrame(
    TelemetryEventContract TriggerTelemetry,
    long FrameSequence,
    Guid AcquisitionId,
    int Width,
    int Height,
    byte[] BmpBytes);

/// <summary>
/// Shared REST + SignalR client for WPF and MAUI remote modes. It does not know
/// platform UI types and never receives local server file paths.
/// </summary>
public sealed class RemoteDeviceClient : IRemoteDeviceClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly RemoteDeviceClientOptions _options;
    private readonly HubConnection _hubConnection;
    private readonly SemaphoreSlim _liveDownloadLock = new(1, 1);
    private long _lastLiveSequence = -1;
    private bool _disposed;

    public RemoteDeviceClient(HttpClient httpClient, RemoteDeviceClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        _httpClient = httpClient;
        _options = options;
        _httpClient.BaseAddress ??= EnsureTrailingSlash(options.BaseAddress);
        var clientId = string.IsNullOrWhiteSpace(options.ClientId)
            ? $"xray-client-{Guid.NewGuid():N}"
            : options.ClientId.Trim();
        _httpClient.DefaultRequestHeaders.Remove(DeviceProtocolHeaders.ClientId);
        _httpClient.DefaultRequestHeaders.Add(DeviceProtocolHeaders.ClientId, clientId);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Remove(DeviceProtocolHeaders.ApiKey);
            _httpClient.DefaultRequestHeaders.Add(DeviceProtocolHeaders.ApiKey, options.ApiKey.Trim());
        }

        var hubUri = new Uri(EnsureTrailingSlash(options.BaseAddress),
            DeviceApiRoutes.EventsHub.TrimStart('/'));
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUri, connection =>
            {
                connection.Headers[DeviceProtocolHeaders.ClientId] = clientId;
                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                    connection.Headers[DeviceProtocolHeaders.ApiKey] = options.ApiKey.Trim();
                else if (options.ApiKeyProvider is not null)
                    connection.AccessTokenProvider = () =>
                        options.ApiKeyProvider(CancellationToken.None);
            })
            .WithAutomaticReconnect()
            .Build();
        _hubConnection.On<DeviceStatusEventContract>(
            DeviceEventNames.StatusChanged,
            status => StatusChanged?.Invoke(status.Device));
        _hubConnection.On<TelemetryEventContract>(
            DeviceEventNames.TelemetryReceived,
            HandleTelemetryAsync);
        _hubConnection.On<SnapProgressEventContract>(
            DeviceEventNames.SnapProgress,
            progress => SnapProgressReceived?.Invoke(progress));
        _hubConnection.On<HardwareStateEventContract>(
            DeviceEventNames.HardwareStateChanged,
            state => HardwareStateReceived?.Invoke(state));
        _hubConnection.On<FrameReadyEventContract>(
            DeviceEventNames.FrameReady,
            frame => FrameReadyReceived?.Invoke(frame));
        _hubConnection.On<GrabFrameReadyEventContract>(
            DeviceEventNames.GrabFrameReady,
            frame => GrabFrameReadyReceived?.Invoke(frame));
        _hubConnection.On<GrabFrozenEventContract>(
            DeviceEventNames.GrabFrozen,
            frozen => GrabFrozenReceived?.Invoke(frozen));
        _hubConnection.On<ExposureEndedEventContract>(
            DeviceEventNames.ExposureEnded,
            ended => ExposureEndedReceived?.Invoke(ended));
        _hubConnection.On<DeviceErrorEventContract>(
            DeviceEventNames.ErrorOccurred,
            error => ErrorReceived?.Invoke(error));
        _hubConnection.Reconnecting += _ =>
        {
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };
        _hubConnection.Reconnected += _ =>
        {
            Interlocked.Exchange(ref _lastLiveSequence, -1);
            ConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        };
        _hubConnection.Closed += _ =>
        {
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };
    }

    public event Action<DeviceStateContract>? StatusChanged;
    public event Action<TelemetryEventContract>? TelemetryReceived;
    public event Action<RemoteLowFrame>? LowFrameReceived;
    public event Action<SnapProgressEventContract>? SnapProgressReceived;
    public event Action<HardwareStateEventContract>? HardwareStateReceived;
    public event Action<FrameReadyEventContract>? FrameReadyReceived;
    public event Action<GrabFrameReadyEventContract>? GrabFrameReadyReceived;
    public event Action<GrabFrozenEventContract>? GrabFrozenReceived;
    public event Action<ExposureEndedEventContract>? ExposureEndedReceived;
    public event Action<DeviceErrorEventContract>? ErrorReceived;
    public event Action<bool>? ConnectionStateChanged;

    public bool EventsConnected => _hubConnection.State == HubConnectionState.Connected;

    public async Task ValidateProtocolAsync(CancellationToken cancellationToken = default)
    {
        var protocol = await GetJsonAsync<DeviceProtocolContract>(
            DeviceApiRoutes.Protocol.TrimStart('/'),
            cancellationToken).ConfigureAwait(false);
        if (protocol.ProtocolVersion != DeviceProtocolVersion.Current)
            throw new InvalidDataException(
                $"DeviceHost protokol sürümü uyumsuz. Beklenen {DeviceProtocolVersion.Current}, alınan {protocol.ProtocolVersion}.");
        if (!string.Equals(protocol.Service, "XRaySystem.DeviceHost", StringComparison.Ordinal) ||
            !string.Equals(protocol.DeviceId, _options.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bağlanılan servis veya DeviceId beklenen XRaySystem DeviceHost ile uyuşmuyor.");

        var operations = protocol.AcquisitionOperations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modes = protocol.SnapFrameModes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!new[] { "Snap", "Grab", "Freeze", "Abort" }.All(operations.Contains) ||
            !new[] { "Raw", "Low" }.All(modes.Contains))
            throw new InvalidDataException("DeviceHost zorunlu acquisition yeteneklerini bildirmiyor.");
    }

    public async Task ConnectEventsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_hubConnection.State != HubConnectionState.Disconnected) return;
        await _hubConnection.StartAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastLiveSequence, -1);
        ConnectionStateChanged?.Invoke(true);
    }

    public async Task DisconnectEventsAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _hubConnection.State == HubConnectionState.Disconnected) return;
        await _hubConnection.StopAsync(cancellationToken).ConfigureAwait(false);
        ConnectionStateChanged?.Invoke(false);
    }

    public Task<DeviceStateContract> GetStateAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<DeviceStateContract>(DeviceRoute(), cancellationToken);

    public Task<DeviceCommandResult> ConnectDeviceAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync<DeviceCommandResult>("connect", new DeviceCommandRequest(Guid.NewGuid()), cancellationToken);

    public Task<DeviceCommandResult> DisconnectDeviceAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync<DeviceCommandResult>("disconnect", new DeviceCommandRequest(Guid.NewGuid()), cancellationToken);

    public Task<DeviceCommandResult> StartLiveAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync<DeviceCommandResult>("live/start", new DeviceCommandRequest(Guid.NewGuid()), cancellationToken);

    public Task<DeviceCommandResult> StopLiveAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync<DeviceCommandResult>("live/stop", new DeviceCommandRequest(Guid.NewGuid()), cancellationToken);

    public Task<DeviceCommandResult> StartGrabAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync<DeviceCommandResult>("grab/start", new DeviceCommandRequest(Guid.NewGuid()), cancellationToken);

    public Task<DeviceCommandResult> FreezeGrabAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync<DeviceCommandResult>("grab/freeze", new DeviceCommandRequest(Guid.NewGuid()), cancellationToken);

    public Task<DeviceCommandResult> AbortAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync<DeviceCommandResult>("abort", new DeviceCommandRequest(Guid.NewGuid()), cancellationToken);

    public Task<DeviceCommandResult> UpdateParametersAsync(
        AcquisitionParametersContract parameters,
        CancellationToken cancellationToken = default) =>
        UpdateParametersAsync(parameters, "IndustrialQaPhantom", cancellationToken);

    public Task<DeviceCommandResult> UpdateParametersAsync(
        AcquisitionParametersContract parameters,
        string profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var request = new UpdateAcquisitionParametersRequest(
            Guid.NewGuid(),
            parameters.VoltageKv,
            parameters.PowerW,
            parameters.ExposureTimeMs,
            profile);
        return SendCommandAsync<DeviceCommandResult>("parameters", request, cancellationToken);
    }

    public Task<SnapCommandResult> SnapAsync(CancellationToken cancellationToken = default) =>
        SnapAsync(SnapFrameModeContract.Raw, cancellationToken);

    public Task<SnapCommandResult> SnapAsync(
        SnapFrameModeContract frameMode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(frameMode))
            throw new ArgumentOutOfRangeException(nameof(frameMode));
        return SendCommandAsync<SnapCommandResult>(
            "snap",
            new SnapCommandRequest(Guid.NewGuid(), frameMode),
            cancellationToken);
    }

    public Task<IReadOnlyList<RemoteImageMetadataContract>> GetImagesAsync(
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<RemoteImageMetadataContract>>(
            $"{DeviceRoute()}/images",
            cancellationToken);

    public Task<byte[]> GetLowImageAsync(Guid imageId, CancellationToken cancellationToken = default) =>
        GetBytesAsync($"api/images/{imageId:D}?mode=low", "image/bmp", cancellationToken);

    public Task<byte[]> GetRawImageAsync(Guid imageId, CancellationToken cancellationToken = default) =>
        GetBytesAsync(
            $"api/images/{imageId:D}?mode=raw",
            "application/vnd.xraysystem.gray16",
            cancellationToken);

    public Task<RemoteLowFrame?> GetLatestLiveFrameAsync(
        TelemetryEventContract telemetry,
        CancellationToken cancellationToken = default) =>
        GetLatestGrabFrameAsync(telemetry, cancellationToken);

    public async Task<RemoteLowFrame?> GetLatestGrabFrameAsync(
        TelemetryEventContract telemetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (!telemetry.LowFrameAvailable) return null;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{DeviceRoute()}/grab/latest?afterSequence={Volatile.Read(ref _lastLiveSequence)}");
        await ApplyDynamicAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureRemoteSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureContentType(response, "image/bmp");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("Remote GRAB low frame boş döndü.");

        var sequence = ReadRequiredHeader<long>(response, "X-XRay-Sequence", long.TryParse);
        var acquisitionId = ReadRequiredHeader<Guid>(response, "X-XRay-Acquisition-Id", Guid.TryParse);
        var width = ReadRequiredHeader<int>(response, "X-XRay-Width", int.TryParse);
        var height = ReadRequiredHeader<int>(response, "X-XRay-Height", int.TryParse);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Remote GRAB low frame boyut başlıkları geçersiz.");

        Interlocked.Exchange(ref _lastLiveSequence, sequence);
        return new RemoteLowFrame(telemetry, sequence, acquisitionId, width, height, bytes);
    }

    private async Task HandleTelemetryAsync(TelemetryEventContract telemetry)
    {
        TelemetryReceived?.Invoke(telemetry);
        if (!telemetry.LowFrameAvailable) return;

        await _liveDownloadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (telemetry.SequenceNumber <= Volatile.Read(ref _lastLiveSequence)) return;
            var frame = await GetLatestGrabFrameAsync(telemetry).ConfigureAwait(false);
            if (frame is not null) LowFrameReceived?.Invoke(frame);
        }
        finally
        {
            _liveDownloadLock.Release();
        }
    }

    private async Task<T> SendCommandAsync<T>(
        string operation,
        object request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{DeviceRoute()}/{operation}")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        await ApplyDynamicAuthenticationAsync(message, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetJsonAsync<T>(string route, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        await ApplyDynamicAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> GetBytesAsync(
        string route,
        string expectedContentType,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        await ApplyDynamicAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureRemoteSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureContentType(response, expectedContentType);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("Remote binary cevap boş döndü.");
        return bytes;
    }

    private async Task ApplyDynamicAuthenticationAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey) || _options.ApiKeyProvider is null) return;
        var apiKey = await _options.ApiKeyProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey)) return;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureRemoteSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("DeviceHost boş veya geçersiz JSON döndürdü.");
    }

    private static async Task EnsureRemoteSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var statusCode = response.StatusCode;
        var errorCode = statusCode switch
        {
            HttpStatusCode.Unauthorized => "unauthorized",
            HttpStatusCode.Forbidden => "forbidden",
            HttpStatusCode.RequestTimeout => "request_timeout",
            HttpStatusCode.Conflict => "conflict",
            HttpStatusCode.TooManyRequests => "rate_limited",
            HttpStatusCode.ServiceUnavailable => "service_unavailable",
            _ when (int)statusCode == 423 => "device_locked",
            _ when (int)statusCode == 507 => "persistence_failed",
            _ => "remote_request_failed"
        };
        var message = $"DeviceHost isteği başarısız oldu ({(int)statusCode}).";
        if (response.Content.Headers.ContentLength is not > 65_536)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    errorCode = error.GetString() ?? errorCode;
                if (TryReadSafeMessage(root, "detail", out var detail) ||
                    TryReadSafeMessage(root, "message", out detail) ||
                    TryReadSafeMessage(root, "title", out detail))
                    message = detail;
            }
            catch (JsonException)
            {
                // Non-JSON server/proxy responses are reduced to the safe status fallback.
            }
        }

        throw new RemoteDeviceException(statusCode, errorCode, message);
    }

    private static bool TryReadSafeMessage(JsonElement root, string propertyName, out string message)
    {
        message = string.Empty;
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        var candidate = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        message = candidate.Length <= 512 ? candidate : candidate[..512];
        return true;
    }

    private static void EnsureContentType(HttpResponseMessage response, string expected)
    {
        var actual = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Beklenen content-type '{expected}', alınan '{actual ?? "(yok)"}'.");
    }

    private delegate bool TryParseHeader<T>(string? value, out T result);

    private static T ReadRequiredHeader<T>(
        HttpResponseMessage response,
        string headerName,
        TryParseHeader<T> tryParse)
    {
        if (response.Headers.TryGetValues(headerName, out var values) &&
            tryParse(values.FirstOrDefault(), out var parsed))
            return parsed;

        throw new InvalidDataException($"Remote GRAB low frame '{headerName}' başlığı eksik veya geçersiz.");
    }

    private string DeviceRoute() =>
        $"api/devices/{Uri.EscapeDataString(_options.DeviceId)}";

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _hubConnection.DisposeAsync().ConfigureAwait(false);
        _liveDownloadLock.Dispose();
    }
}
