namespace XRaySystem.Domain.WindowLevel;

/// Testable latest-request-wins worker. It retains at most one active and one pending request.
public sealed class LatestWindowLevelScheduler<T> : IAsyncDisposable
{
    private readonly Func<T, CancellationToken, Task> _work;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Pending? _pending;
    private bool _running;
    private long _generation;
    private long _version;
    private CancellationTokenSource? _activeCancellation;

    private sealed record Pending(T Value, long Generation, long Version);

    public LatestWindowLevelScheduler(Func<T, CancellationToken, Task> work) => _work = work;

    public (long Generation, long Version) Submit(T value)
    {
        lock (_gate)
        {
            var request = new Pending(value, ++_generation, ++_version);
            _pending = request;
            _activeCancellation?.Cancel();
            if (!_running)
            {
                _running = true;
                _ = Task.Run(DrainAsync);
            }
            return (request.Generation, request.Version);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _pending = null;
            _generation++;
            _version++;
            _activeCancellation?.Cancel();
        }
    }

    private async Task DrainAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            Pending? request;
            lock (_gate)
            {
                request = _pending;
                _pending = null;
                if (request == null) { _running = false; return; }
            }

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            lock (_gate) _activeCancellation = requestCancellation;
            try { await _work(request.Value, requestCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested || _shutdown.IsCancellationRequested) { }
            finally { lock (_gate) if (ReferenceEquals(_activeCancellation, requestCancellation)) _activeCancellation = null; }
        }
        lock (_gate) _running = false;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        lock (_gate) _pending = null;
        lock (_gate) _activeCancellation?.Cancel();
        while (true)
        {
            lock (_gate) if (!_running) break;
            await Task.Delay(1).ConfigureAwait(false);
        }
        _shutdown.Dispose();
    }
}
