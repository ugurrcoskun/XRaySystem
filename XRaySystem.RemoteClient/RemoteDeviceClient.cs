using System.Net;
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
        _hubConnection.On<DeviceErrorEventContract>(
            DeviceEventNames.ErrorOccurred,
            error => ErrorReceived?.Invoke(error));
        _hubConnection.Reconnected += _ =>
        {
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
    public event Action<DeviceErrorEventContract>? ErrorReceived;
    public event Action<bool>? ConnectionStateChanged;

    public bool EventsConnected => _hubConnection.State == HubConnectionState.Connected;

    public async Task ConnectEventsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_hubConnection.State != HubConnectionState.Disconnected) return;
        await _hubConnection.StartAsync(cancellationToken).ConfigureAwait(false);
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
        SendCommandAsync<SnapCommandResult>("snap", new DeviceCommandRequest(Guid.NewGuid()), cancellationToken);

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

    public async Task<RemoteLowFrame?> GetLatestLiveFrameAsync(
        TelemetryEventContract telemetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (!telemetry.LowFrameAvailable) return null;
        using var response = await _httpClient.GetAsync(
            $"{DeviceRoute()}/live/latest?afterSequence={Volatile.Read(ref _lastLiveSequence)}",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        EnsureContentType(response, "image/bmp");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("Remote low live frame boş döndü.");

        var sequence = ReadRequiredHeader<long>(response, "X-XRay-Sequence", long.TryParse);
        var acquisitionId = ReadRequiredHeader<Guid>(response, "X-XRay-Acquisition-Id", Guid.TryParse);
        var width = ReadRequiredHeader<int>(response, "X-XRay-Width", int.TryParse);
        var height = ReadRequiredHeader<int>(response, "X-XRay-Height", int.TryParse);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Remote low live frame boyut başlıkları geçersiz.");

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
            var frame = await GetLatestLiveFrameAsync(telemetry).ConfigureAwait(false);
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
        using var response = await _httpClient.PostAsJsonAsync(
            $"{DeviceRoute()}/{operation}",
            request,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetJsonAsync<T>(string route, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var response = await _httpClient.GetAsync(route, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> GetBytesAsync(
        string route,
        string expectedContentType,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var response = await _httpClient.GetAsync(route, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureContentType(response, expectedContentType);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("Remote binary cevap boş döndü.");
        return bytes;
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("DeviceHost boş veya geçersiz JSON döndürdü.");
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

        throw new InvalidDataException($"Remote low live frame '{headerName}' başlığı eksik veya geçersiz.");
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
