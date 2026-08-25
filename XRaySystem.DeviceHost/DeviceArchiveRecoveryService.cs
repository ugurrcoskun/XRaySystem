namespace XRaySystem.DeviceHost;

/// <summary>
/// Rehydrates the bounded remote buffer from verified durable acquisition
/// manifests. Corrupt or incomplete archive entries remain unavailable.
/// </summary>
public sealed class DeviceArchiveRecoveryService : IHostedService
{
    private readonly IDeviceAcquisitionArchive _archive;
    private readonly RemoteImageBuffer _buffer;
    private readonly RemoteImageBufferOptions _bufferOptions;
    private readonly ILogger<DeviceArchiveRecoveryService> _logger;

    public DeviceArchiveRecoveryService(
        IDeviceAcquisitionArchive archive,
        RemoteImageBuffer buffer,
        RemoteImageBufferOptions bufferOptions,
        ILogger<DeviceArchiveRecoveryService> logger)
    {
        _archive = archive;
        _buffer = buffer;
        _bufferOptions = bufferOptions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var recoveredCount = 0;
        foreach (var archived in _archive.LoadRecent(_bufferOptions.ImageLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _buffer.Add(archived.Metadata, archived.Frame, archived.LowPreviewBmp);
                recoveredCount++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                _logger.LogWarning(
                    ex,
                    "Archived acquisition could not enter remote buffer: ImageId={ImageId} AcquisitionId={AcquisitionId}",
                    archived.Metadata.ImageId,
                    archived.Metadata.AcquisitionId);
            }
        }

        _logger.LogInformation(
            "Device acquisition archive recovery completed: RecoveredCount={RecoveredCount}",
            recoveredCount);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
