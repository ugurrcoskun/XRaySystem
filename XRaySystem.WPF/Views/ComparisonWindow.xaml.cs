using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XRaySystem.WPF.ViewModels;

namespace XRaySystem.WPF.Views;

/// <summary>
/// Senkronize zoom/pan, koordinat cetvelleri ve metadata overlay'i ile
/// maksimum 4 X-Ray görselini yan yana karşılaştırır.
/// </summary>
public partial class ComparisonWindow : Window
{
    // ──────────────────────────────────────────────
    // Shared zoom/pan state (all panels sync to this)
    // ──────────────────────────────────────────────
    private double _scale = 1.0;
    private double _translateX = 0.0;
    private double _translateY = 0.0;
    private const double MinScale = 0.05;
    private const double MaxScale = 200.0;

    // Per-panel transform references
    private readonly List<ScaleTransform> _scaleTransforms = new();
    private readonly List<TranslateTransform> _translateTransforms = new();
    private readonly List<Border> _viewportBorders = new();

    // Pan state
    private bool _isPanning;
    private Point _panStart;
    private double _panOriginX, _panOriginY;

    private readonly List<ComparisonItem> _items;

    public ComparisonWindow(IEnumerable<ComparisonItem> items)
    {
        InitializeComponent();
        _items = items.ToList();
        BuildGrid();
        ImageCountLabel.Text = $"{_items.Count} GÖRSEL KARŞILAŞTIRILIYOR";
        UpdateZoomLabel();
        Loaded += (_, _) => BuildRulers();
    }

    // ──────────────────────────────────────────────
    // Grid Builder
    // ──────────────────────────────────────────────
    private void BuildGrid()
    {
        int count = _items.Count;

        // Determine layout
        (int rows, int cols) = count switch
        {
            1 => (1, 1),
            2 => (1, 2),
            3 => (1, 3),
            _ => (2, 2)
        };

        for (int r = 0; r < rows; r++)
            ComparisonGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < cols; c++)
            ComparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            var panel = BuildPanel(_items[i], i);
            Grid.SetRow(panel, row);
            Grid.SetColumn(panel, col);
            ComparisonGrid.Children.Add(panel);
        }
    }

    private UIElement BuildPanel(ComparisonItem item, int index)
    {
        // Outer border with thin separator
        var outer = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 32, 64)),
            BorderThickness = new Thickness(index == 0 ? 0 : 1, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(6, 9, 16)),
            ClipToBounds = true
        };

        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) }); // X ruler
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // label row
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // image
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); // Y ruler
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── X Ruler (top) ──
        var xRulerCanvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromRgb(8, 14, 26)),
            ClipToBounds = true
        };
        Grid.SetRow(xRulerCanvas, 0);
        Grid.SetColumn(xRulerCanvas, 1);
        outerGrid.Children.Add(xRulerCanvas);

        // ── Y Ruler (left) ──
        var yRulerCanvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromRgb(8, 14, 26)),
            ClipToBounds = true
        };
        Grid.SetRow(yRulerCanvas, 2);
        Grid.SetColumn(yRulerCanvas, 0);
        outerGrid.Children.Add(yRulerCanvas);

        // ── Image viewport ──
        var viewport = new Border
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            Cursor = Cursors.Cross
        };
        Grid.SetRow(viewport, 2);
        Grid.SetColumn(viewport, 1);
        _viewportBorders.Add(viewport);

        var scaleT = new ScaleTransform(_scale, _scale);
        var translateT = new TranslateTransform(_translateX, _translateY);
        _scaleTransforms.Add(scaleT);
        _translateTransforms.Add(translateT);

        var tg = new TransformGroup();
        tg.Children.Add(scaleT);
        tg.Children.Add(translateT);

        var imageContainer = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), IsHitTestVisible = false };
        imageContainer.RenderTransform = tg;

        // Load full-resolution original image for comparison viewport
        BitmapSource? bmp = null;
        if (!string.IsNullOrEmpty(item.ImagePath) && System.IO.File.Exists(item.ImagePath))
        {
            try
            {
                var tiff = new BitmapImage();
                tiff.BeginInit();
                tiff.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
                tiff.CacheOption = BitmapCacheOption.OnLoad;
                tiff.EndInit();
                tiff.Freeze();
                bmp = tiff;
            }
            catch { /* fallback */ }
        }
        if (bmp == null)
        {
            bmp = item.Thumbnail;
        }

        var img = new Image
        {
            Source = bmp,
            Stretch = Stretch.Uniform
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        imageContainer.Children.Add(img);

        // Crosshair
        var crosshair = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 150,0 L 150,300 M 0,150 L 300,150"),
            Stroke = new SolidColorBrush(Color.FromArgb(60, 0, 170, 255)),
            StrokeThickness = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false
        };
        imageContainer.Children.Add(crosshair);

        viewport.Child = imageContainer;

        // Zoom/pan events on viewport
        viewport.MouseWheel += (s, e) =>
        {
            if (e.Delta == 0) return;
            var pos = e.GetPosition(viewport);
            double factor = Math.Exp(e.Delta / 1200.0);
            ApplyZoom(factor, pos, viewport);
            e.Handled = true;
        };
        viewport.MouseLeftButtonDown += (s, e) =>
        {
            viewport.CaptureMouse();
            _panStart = e.GetPosition(viewport);
            _panOriginX = _translateX;
            _panOriginY = _translateY;
            _isPanning = false;
        };
        viewport.MouseMove += (s, e) =>
        {
            if (!viewport.IsMouseCaptured) return;
            var cur = e.GetPosition(viewport);
            var delta = cur - _panStart;
            if (!_isPanning && delta.Length < 6) return;
            _isPanning = true;
            _translateX = _panOriginX + delta.X;
            _translateY = _panOriginY + delta.Y;
            SyncAllTransforms();
        };
        viewport.MouseLeftButtonUp += (s, _) => { viewport.ReleaseMouseCapture(); _isPanning = false; };
        viewport.LostMouseCapture += (s, _) => _isPanning = false;

        // ── Metadata overlay (bottom-left of image area) ──
        var metaPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 0, 10),
            IsHitTestVisible = false
        };
        Grid.SetRow(metaPanel, 2);
        Grid.SetColumn(metaPanel, 1);

        var metaBg = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 6, 12, 26)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 0, 10),
            IsHitTestVisible = false
        };
        Grid.SetRow(metaBg, 2);
        Grid.SetColumn(metaBg, 1);

        var metaStack = new StackPanel();
        void AddMetaToStack(string label, string value, string unit, Color col)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label + " ", Foreground = new SolidColorBrush(Color.FromRgb(55, 80, 120)), FontSize = 10, FontFamily = new FontFamily("Consolas") });
            sp.Children.Add(new TextBlock { Text = value, Foreground = new SolidColorBrush(col), FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas") });
            sp.Children.Add(new TextBlock { Text = " " + unit, Foreground = new SolidColorBrush(Color.FromRgb(55, 80, 120)), FontSize = 10, FontFamily = new FontFamily("Consolas") });
            metaStack.Children.Add(sp);
        }

        AddMetaToStack("kV", $"{item.Voltage:F0}", "kV", Color.FromRgb(0, 200, 255));
        AddMetaToStack("P", $"{item.PowerW:F0}", "W", Color.FromRgb(0, 220, 140));
        AddMetaToStack("ms", $"{item.ExposureTime:F0}", "ms", Color.FromRgb(255, 180, 50));
        AddMetaToStack("T", item.Dto.Timestamp.ToString("HH:mm:ss"), "", Color.FromRgb(130, 150, 170));

        metaBg.Child = metaStack;

        // ── Header label ──
        var headerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(8, 14, 26)),
            Padding = new Thickness(12, 5, 12, 5)
        };
        Grid.SetRow(headerBorder, 1);
        Grid.SetColumnSpan(headerBorder, 2);
        var headerText = new TextBlock
        {
            Text = $"#{index + 1}  ·  {item.Dto.Timestamp:yyyy-MM-dd HH:mm:ss}",
            Foreground = new SolidColorBrush(Color.FromRgb(0, 150, 220)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas")
        };
        headerBorder.Child = headerText;

        outerGrid.Children.Add(viewport);
        outerGrid.Children.Add(metaBg);
        outerGrid.Children.Add(headerBorder);
        // xRulerCanvas and yRulerCanvas were already added above (lines 108, 118)

        outer.Child = outerGrid;
        return outer;
    }

    // ──────────────────────────────────────────────
    // Rulers — drawn after layout pass
    // ──────────────────────────────────────────────
    private void BuildRulers()
    {
        // We tag canvases; walk the tree
        foreach (UIElement child in ComparisonGrid.Children)
            DrawRulersForPanel(child);
    }

    private static void DrawRulersForPanel(UIElement panelRoot)
    {
        if (panelRoot is not Border outer) return;
        if (outer.Child is not Grid outerGrid) return;

        Canvas? xCanvas = null, yCanvas = null;
        foreach (UIElement el in outerGrid.Children)
        {
            if (el is Canvas c)
            {
                int col = Grid.GetColumn(c);
                int row = Grid.GetRow(c);
                if (row == 0 && col == 1) xCanvas = c;
                if (row == 2 && col == 0) yCanvas = c;
            }
        }

        xCanvas?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        xCanvas?.Arrange(new Rect(xCanvas.DesiredSize));

        if (xCanvas != null) DrawXRuler(xCanvas);
        if (yCanvas != null) DrawYRuler(yCanvas);
    }

    private static void DrawXRuler(Canvas canvas)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 400;
        int step = 100;
        for (int px = 0; px <= (int)w; px += step)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = px, Y1 = 12, X2 = px, Y2 = 20,
                Stroke = new SolidColorBrush(Color.FromRgb(30, 60, 100)),
                StrokeThickness = 1
            };
            canvas.Children.Add(line);
            var tb = new TextBlock
            {
                Text = px.ToString(),
                Foreground = new SolidColorBrush(Color.FromRgb(40, 75, 115)),
                FontSize = 8,
                FontFamily = new FontFamily("Consolas")
            };
            Canvas.SetLeft(tb, px + 2);
            Canvas.SetTop(tb, 2);
            canvas.Children.Add(tb);
        }
    }

    private static void DrawYRuler(Canvas canvas)
    {
        canvas.Children.Clear();
        double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 400;
        int step = 100;
        for (int py = 0; py <= (int)h; py += step)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = 16, Y1 = py, X2 = 28, Y2 = py,
                Stroke = new SolidColorBrush(Color.FromRgb(30, 60, 100)),
                StrokeThickness = 1
            };
            canvas.Children.Add(line);

            var tb = new TextBlock
            {
                Text = py.ToString(),
                Foreground = new SolidColorBrush(Color.FromRgb(40, 75, 115)),
                FontSize = 8,
                FontFamily = new FontFamily("Consolas"),
                RenderTransformOrigin = new Point(0, 0),
                RenderTransform = new RotateTransform(-90)
            };
            Canvas.SetLeft(tb, 2);
            Canvas.SetTop(tb, py - 2);
            canvas.Children.Add(tb);
        }
    }

    // ──────────────────────────────────────────────
    // Zoom helpers
    // ──────────────────────────────────────────────
    private void ApplyZoom(double factor, Point center, Border viewport)
    {
        double oldScale = _scale;
        double newScale = Math.Clamp(oldScale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - oldScale) < 1e-9) return;

        double originX = viewport.ActualWidth / 2.0;
        double originY = viewport.ActualHeight / 2.0;
        double imgX = (center.X - originX - _translateX) / oldScale + originX;
        double imgY = (center.Y - originY - _translateY) / oldScale + originY;

        _scale = newScale;
        _translateX = center.X - originX - (imgX - originX) * newScale;
        _translateY = center.Y - originY - (imgY - originY) * newScale;

        SyncAllTransforms();
    }

    private void SyncAllTransforms()
    {
        for (int i = 0; i < _scaleTransforms.Count; i++)
        {
            _scaleTransforms[i].ScaleX = _scale;
            _scaleTransforms[i].ScaleY = _scale;
            _translateTransforms[i].X = _translateX;
            _translateTransforms[i].Y = _translateY;
        }
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = $"{_scale * 100:0}%";

    // ──────────────────────────────────────────────
    // Button handlers
    // ──────────────────────────────────────────────
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ApplyCenterZoom(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ApplyCenterZoom(1.0 / 1.25);

    private void ApplyCenterZoom(double factor)
    {
        // Use first viewport center as reference
        if (_viewportBorders.Count == 0) return;
        var vp = _viewportBorders[0];
        var center = new Point(vp.ActualWidth / 2, vp.ActualHeight / 2);
        ApplyZoom(factor, center, vp);
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        _scale = 1.0;
        _translateX = 0;
        _translateY = 0;
        SyncAllTransforms();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
