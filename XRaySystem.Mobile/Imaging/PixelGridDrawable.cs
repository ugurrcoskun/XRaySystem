using Microsoft.Maui.Graphics;

namespace XRaySystem.Mobile.Imaging;

internal sealed class PixelGridDrawable : IDrawable
{
    private const double MinimumVisiblePixelSize = 6.0;

    private int _pixelWidth;
    private int _pixelHeight;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _scale;
    private double _translationX;
    private double _translationY;

    public bool Configure(
        int pixelWidth,
        int pixelHeight,
        double viewportWidth,
        double viewportHeight,
        double scale,
        double translationX,
        double translationY)
    {
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        _scale = scale;
        _translationX = translationX;
        _translationY = translationY;

        return TryGetGeometry(out _, out _, out _);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (!TryGetGeometry(out var pixelSize, out var contentLeft, out var contentTop))
            return;

        var firstColumn = Math.Max(0, (int)Math.Ceiling(-contentLeft / pixelSize));
        var lastColumn = Math.Min(
            _pixelWidth,
            (int)Math.Floor((_viewportWidth - contentLeft) / pixelSize));
        var firstRow = Math.Max(0, (int)Math.Ceiling(-contentTop / pixelSize));
        var lastRow = Math.Min(
            _pixelHeight,
            (int)Math.Floor((_viewportHeight - contentTop) / pixelSize));

        canvas.SaveState();
        canvas.ClipRectangle(0, 0, (float)_viewportWidth, (float)_viewportHeight);
        canvas.StrokeColor = Color.FromArgb("#A038506F");
        canvas.StrokeSize = 0.75f;

        var contentBottom = contentTop + _pixelHeight * pixelSize;
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            var x = (float)(contentLeft + column * pixelSize);
            canvas.DrawLine(x, (float)contentTop, x, (float)contentBottom);
        }

        var contentRight = contentLeft + _pixelWidth * pixelSize;
        for (var row = firstRow; row <= lastRow; row++)
        {
            var y = (float)(contentTop + row * pixelSize);
            canvas.DrawLine((float)contentLeft, y, (float)contentRight, y);
        }

        canvas.RestoreState();
    }

    private bool TryGetGeometry(
        out double pixelSize,
        out double contentLeft,
        out double contentTop)
    {
        pixelSize = 0;
        contentLeft = 0;
        contentTop = 0;
        if (_pixelWidth <= 0 || _pixelHeight <= 0 ||
            _viewportWidth <= 0 || _viewportHeight <= 0 ||
            _scale <= 1 || !double.IsFinite(_scale))
            return false;

        var fitScale = Math.Min(
            _viewportWidth / _pixelWidth,
            _viewportHeight / _pixelHeight);
        pixelSize = fitScale * _scale;
        if (!double.IsFinite(pixelSize) || pixelSize < MinimumVisiblePixelSize)
            return false;

        var renderedWidth = _pixelWidth * fitScale;
        var renderedHeight = _pixelHeight * fitScale;
        var baseLeft = (_viewportWidth - renderedWidth) / 2.0;
        var baseTop = (_viewportHeight - renderedHeight) / 2.0;
        var originX = _viewportWidth / 2.0;
        var originY = _viewportHeight / 2.0;
        contentLeft = originX + (baseLeft - originX) * _scale + _translationX;
        contentTop = originY + (baseTop - originY) * _scale + _translationY;
        return true;
    }
}
