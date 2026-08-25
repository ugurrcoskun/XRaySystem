namespace XRaySystem.Infrastructure.Concurrency;

/// <summary>
/// Runs at most one operation while retaining at most one pending value. A new
/// value atomically replaces an older pending value so high-rate frame sources
/// cannot grow an unbounded work queue.
/// </summary>
public sealed class LatestValueWorker<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<T, CancellationToken, ValueTask> _processor;
    private readonly Action<T, Exception>? _onError;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stoppingToken;
    private T? _pending;
    private bool _hasPending;
    private bool _workerRunning;
    private bool _disposed;
    private long _acceptedCount;
    private long _replacedCount;
    private long _completedCount;
    private long _faultedCount;

    public LatestValueWorker(
        Func<T, CancellationToken, ValueTask> processor,
        Action<T, Exception>? onError = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _onError = onError;
        _stoppingToken = _stopping.Token;
    }

    public long AcceptedCount => Interlocked.Read(ref _acceptedCount);
    public long ReplacedCount => Interlocked.Read(ref _replacedCount);
    public long CompletedCount => Interlocked.Read(ref _completedCount);
    public long FaultedCount => Interlocked.Read(ref _faultedCount);

    public bool TryPost(T value)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            Interlocked.Increment(ref _acceptedCount);
            if (_hasPending)
                Interlocked.Increment(ref _replacedCount);
            _pending = value;
            _hasPending = true;
            if (_workerRunning) return true;
            _workerRunning = true;
        }

        _ = Task.Run(DrainAsync);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _hasPending = false;
            _pending = default;
            _stopping.Cancel();
        }

        _stopping.Dispose();
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (_disposed || !_hasPending)
                {
                    _workerRunning = false;
                    return;
                }

                value = _pending!;
                _pending = default;
                _hasPending = false;
            }

            try
            {
                await _processor(value, _stoppingToken).ConfigureAwait(false);
                Interlocked.Increment(ref _completedCount);
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _faultedCount);
                try { _onError?.Invoke(value, ex); }
                catch { }
            }
        }
    }
}
