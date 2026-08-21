using XRaySystem.Contracts;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.DeviceHost;

public sealed record RemoteImageBufferOptions(
    int ImageLimit,
    long MaxBytes,
    TimeSpan? TimeToLive)
{
    public static RemoteImageBufferOptions Default { get; } = new(
        ImageLimit: 10,
        MaxBytes: 128L * 1024 * 1024,
        TimeToLive: TimeSpan.FromMinutes(30));

    public void EnsureValid()
    {
        if (ImageLimit <= 0) throw new ArgumentOutOfRangeException(nameof(ImageLimit));
        if (MaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBytes));
        if (TimeToLive is { } ttl && ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TimeToLive));
    }
}

/// <summary>
/// Bounded in-memory remote history. It has no dependency on desktop TIFF/SQLite storage.
/// </summary>
public sealed class RemoteImageBuffer
{
    private readonly RemoteImageBufferOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly LinkedList<StoredImage> _order = new();
    private readonly Dictionary<Guid, LinkedListNode<StoredImage>> _byId = new();
    private long _totalBytes;

    public RemoteImageBuffer(RemoteImageBufferOptions options, TimeProvider? timeProvider = null)
    {
        options.EnsureValid();
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Count
    {
        get { lock (_sync) { PurgeExpired(); return _order.Count; } }
    }

    public long TotalBytes
    {
        get { lock (_sync) { PurgeExpired(); return _totalBytes; } }
    }

    public void Add(RemoteImageMetadataContract metadata, RawGray16Frame frame, byte[] lowBmp)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(lowBmp);
        if (metadata.ImageId == Guid.Empty) throw new ArgumentException("ImageId boş olamaz.", nameof(metadata));
        if (lowBmp.Length == 0) throw new ArgumentException("Low preview boş olamaz.", nameof(lowBmp));

        var rawCopy = frame with { Pixels = frame.Pixels.ToArray() };
        var lowCopy = lowBmp.ToArray();
        var entryBytes = checked((long)rawCopy.Pixels.Length * sizeof(ushort) + lowCopy.Length);
        if (entryBytes > _options.MaxBytes)
            throw new InvalidOperationException("Tek remote görüntü buffer byte limitini aşıyor.");

        lock (_sync)
        {
            PurgeExpired();
            if (_byId.TryGetValue(metadata.ImageId, out var existing)) Remove(existing);

            var stored = new StoredImage(metadata, rawCopy, lowCopy, entryBytes, _timeProvider.GetUtcNow());
            var node = _order.AddLast(stored);
            _byId.Add(metadata.ImageId, node);
            _totalBytes += entryBytes;

            while (_order.Count > _options.ImageLimit || _totalBytes > _options.MaxBytes)
                Remove(_order.First!);
        }
    }

    public IReadOnlyList<RemoteImageMetadataContract> GetMetadata()
    {
        lock (_sync)
        {
            PurgeExpired();
            return _order.Reverse().Select(entry => entry.Metadata).ToArray();
        }
    }

    public bool TryGetLow(Guid imageId, out ReadOnlyMemory<byte> lowBmp)
    {
        lock (_sync)
        {
            PurgeExpired();
            if (_byId.TryGetValue(imageId, out var node))
            {
                lowBmp = node.Value.LowBmp;
                return true;
            }
            lowBmp = default;
            return false;
        }
    }

    public bool TryGetRaw(Guid imageId, out RawGray16Frame? frame)
    {
        lock (_sync)
        {
            PurgeExpired();
            if (_byId.TryGetValue(imageId, out var node))
            {
                frame = node.Value.Frame;
                return true;
            }
            frame = null;
            return false;
        }
    }

    private void PurgeExpired()
    {
        if (_options.TimeToLive is not { } ttl) return;
        var threshold = _timeProvider.GetUtcNow() - ttl;
        while (_order.First is { } first && first.Value.StoredAtUtc <= threshold)
            Remove(first);
    }

    private void Remove(LinkedListNode<StoredImage> node)
    {
        _order.Remove(node);
        _byId.Remove(node.Value.Metadata.ImageId);
        _totalBytes -= node.Value.ByteCount;
    }

    private sealed record StoredImage(
        RemoteImageMetadataContract Metadata,
        RawGray16Frame Frame,
        byte[] LowBmp,
        long ByteCount,
        DateTimeOffset StoredAtUtc);
}
