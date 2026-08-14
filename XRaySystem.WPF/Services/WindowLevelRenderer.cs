using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Domain.WindowLevel;

namespace XRaySystem.WPF.Services;

public sealed class WindowLevelRenderer : IAsyncDisposable
{
    private sealed record Request(RawGray16Frame Frame, double Center, double Width, long Generation, long Version);
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly LatestWindowLevelScheduler<Request> _scheduler;
    private readonly object _gate = new();
    private long _generation;
    private long _version;

    public WindowLevelRenderer()
    {
        _dispatcher = global::System.Windows.Application.Current.Dispatcher;
        _scheduler = new LatestWindowLevelScheduler<Request>(RenderAsync);
    }

    public void Submit(RawGray16Frame frame, double center, double width, Action<BitmapSource> assign)
    {
        lock (_gate)
        {
            Assignment = assign;
            _version++;
            _scheduler.Submit(new Request(frame, center, width, _generation, _version));
        }
    }

    public void Invalidate()
    {
        lock (_gate) { _generation++; _version++; }
        _scheduler.Invalidate();
    }

    private async Task RenderAsync(Request request, CancellationToken cancellationToken)
    {
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        var pixels = WindowLevelMapper.Map(request.Frame.Pixels, request.Center, request.Width, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource bitmap;
        try
        {
            bitmap = BitmapSource.Create(request.Frame.Width, request.Frame.Height, 96, 96,
                PixelFormats.Gray8, null, pixels, request.Frame.Width);
            bitmap.Freeze();
        }
        catch
        {
            // Caller retains the raw Gray16 fallback; a failed presentation map is non-destructive.
            return;
        }

        lock (_gate)
        {
            if (request.Generation != _generation || request.Version != _version) return;
        }
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                if (request.Generation != _generation || request.Version != _version) return;
            }
            // The bitmap is frozen, so only this small property assignment runs on the UI thread.
            Assignment?.Invoke(bitmap);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private Action<BitmapSource>? Assignment { get; set; }

    public ValueTask DisposeAsync() => _scheduler.DisposeAsync();
}
