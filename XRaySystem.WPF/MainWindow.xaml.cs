using System.Windows;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XRaySystem.WPF.ViewModels;
using XRaySystem.WPF.Views;

namespace XRaySystem.WPF
{
    public partial class MainWindow : Window
    {
        private bool _startupHistoryLoadStarted;

        // ── Comparison selection (max 4) ──
        private readonly List<ComparisonItem> _comparisonSelection = new();
        private readonly Dictionary<Guid, Button> _compBtnMap = new();


        // DI üzerinden LiveTelemetryViewModel otomatik olarak enjekte edilecek
        public MainWindow(LiveTelemetryViewModel viewModel)
        {
            InitializeComponent();

            DataContext = viewModel;

            Loaded += MainWindow_Loaded;
            Closed += Window_Closed;
            SizeChanged += MainWindow_SizeChanged;
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            if (DataContext is LiveTelemetryViewModel vm) vm.InvalidateWindowLevelRendering();
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (XRayScanBorder != null && XRayScanBorder.IsMouseOver)
                return;

            if (e.OriginalSource is not System.Windows.Controls.TextBox &&
                e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase)
            {
                RootGrid.Focus();
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_startupHistoryLoadStarted || DataContext is not LiveTelemetryViewModel vm)
                return;

            _startupHistoryLoadStarted = true;
            try
            {
                await vm.RefreshHistoryCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                // Keep async-void WPF event paths from surfacing an unhandled exception.
                System.Diagnostics.Debug.WriteLine($"Startup history load failed: {ex}");
            }
        }

        private const double MainMinZoom = 1.0;
        // Kullanıcıya görünür bir üst sınır yoktur; bu yalnız double/renderer taşmasını engeller.
        private const double MainNumericSafetyZoom = 1_000_000.0;
        private double _mainScale = 1.0;
        private double _mainTransX = 0.0;
        private double _mainTransY = 0.0;
        private bool _mainIsMouseDown;
        private Point _mainPanStart;
        private double _mainPanOriginX, _mainPanOriginY;
        private bool _mainIsPanning;

        private void XRayImage_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var border = (System.Windows.Controls.Border)sender;
            if (e.Delta == 0) return;
            Point mousePos = e.GetPosition(border);
            double factor = Math.Exp(e.Delta / 1200.0);
            MainApplyZoom(factor, mousePos, border);
            e.Handled = true;
        }

        private void MainApplyZoom(double factor, Point center, System.Windows.Controls.Border viewport)
        {
            double oldScale = _mainScale;
            double requestedScale = oldScale * factor;
            if (!double.IsFinite(requestedScale))
                requestedScale = MainNumericSafetyZoom;
            double newScale = Math.Clamp(requestedScale, MainMinZoom, MainNumericSafetyZoom);
            if (Math.Abs(newScale - oldScale) < 1e-9) return;

            double originX = viewport.ActualWidth / 2.0;
            double originY = viewport.ActualHeight / 2.0;
            double imgX = (center.X - originX - _mainTransX) / oldScale + originX;
            double imgY = (center.Y - originY - _mainTransY) / oldScale + originY;

            _mainScale = newScale;
            _mainTransX = center.X - originX - (imgX - originX) * newScale;
            _mainTransY = center.Y - originY - (imgY - originY) * newScale;

            if (newScale <= MainMinZoom)
            {
                _mainTransX = 0;
                _mainTransY = 0;
            }
            else
            {
                ClampMainTranslation();
            }

            SyncMainTransforms();
        }

        private void SyncMainTransforms()
        {
            XRayScaleTransform.ScaleX = _mainScale;
            XRayScaleTransform.ScaleY = _mainScale;
            XRayTranslateTransform.X = _mainTransX;
            XRayTranslateTransform.Y = _mainTransY;
            UpdateZoomText();
            UpdateMainCoordinateReadout();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            var center = new Point(XRayScanBorder.ActualWidth / 2, XRayScanBorder.ActualHeight / 2);
            MainApplyZoom(1.25, center, XRayScanBorder);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            var center = new Point(XRayScanBorder.ActualWidth / 2, XRayScanBorder.ActualHeight / 2);
            MainApplyZoom(1.0 / 1.25, center, XRayScanBorder);
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e) => ResetImageView();

        private void ResetImageView()
        {
            _mainScale = 1.0;
            _mainTransX = 0;
            _mainTransY = 0;
            SyncMainTransforms();
        }

        private void XRayImage_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var border = (System.Windows.Controls.Border)sender;
            _mainIsMouseDown = true;
            _mainIsPanning = false;
            _mainPanStart = e.GetPosition(border);
            _mainPanOriginX = _mainTransX;
            _mainPanOriginY = _mainTransY;
            border.CaptureMouse();
        }

        private void XRayImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var border = (System.Windows.Controls.Border)sender;
            UpdateMainPointerCoordinate(e.GetPosition(border));
            if (!_mainIsMouseDown || !border.IsMouseCaptured) return;
            var current = e.GetPosition(border);
            var delta = current - _mainPanStart;
            if (!_mainIsPanning)
            {
                if (delta.Length < 6) return;
                _mainIsPanning = true;
            }
            _mainTransX = _mainPanOriginX + delta.X;
            _mainTransY = _mainPanOriginY + delta.Y;
            ClampMainTranslation();
            SyncMainTransforms();
        }

        private void XRayImage_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
            MainPointerCoordinateText.Text = "POINTER X: —  Y: —";

        private void XRayImage_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var border = (System.Windows.Controls.Border)sender;
            _mainIsMouseDown = false;
            _mainIsPanning = false;
            if (border.IsMouseCaptured)
            {
                border.ReleaseMouseCapture();
            }
        }

        private void XRayImage_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _mainIsMouseDown = false;
            _mainIsPanning = false;
        }

        private void UpdateZoomText() => ZoomPercentText.Text = $"{_mainScale * 100:0}%";

        private void ClampMainTranslation()
        {
            if (_mainScale <= MainMinZoom)
            {
                _mainTransX = 0;
                _mainTransY = 0;
                return;
            }

            var maximumX = Math.Max(0, XRayScanBorder.ActualWidth * (_mainScale - 1) / 2);
            var maximumY = Math.Max(0, XRayScanBorder.ActualHeight * (_mainScale - 1) / 2);
            _mainTransX = Math.Clamp(_mainTransX, -maximumX, maximumX);
            _mainTransY = Math.Clamp(_mainTransY, -maximumY, maximumY);
        }

        private bool TryViewportToImagePixel(Point viewportPoint, out int pixelX, out int pixelY)
        {
            pixelX = 0;
            pixelY = 0;
            if (XRayImage.Source is not BitmapSource bitmap ||
                bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 ||
                ImageLayer.ActualWidth <= 0 || ImageLayer.ActualHeight <= 0)
                return false;

            var originX = XRayScanBorder.ActualWidth / 2.0;
            var originY = XRayScanBorder.ActualHeight / 2.0;
            var layerX = (viewportPoint.X - originX - _mainTransX) / _mainScale + originX;
            var layerY = (viewportPoint.Y - originY - _mainTransY) / _mainScale + originY;
            var fitScale = Math.Min(
                ImageLayer.ActualWidth / bitmap.PixelWidth,
                ImageLayer.ActualHeight / bitmap.PixelHeight);
            if (fitScale <= 0 || !double.IsFinite(fitScale))
                return false;

            var renderedWidth = bitmap.PixelWidth * fitScale;
            var renderedHeight = bitmap.PixelHeight * fitScale;
            var offsetX = (ImageLayer.ActualWidth - renderedWidth) / 2.0;
            var offsetY = (ImageLayer.ActualHeight - renderedHeight) / 2.0;
            var sourceX = (layerX - offsetX) / fitScale;
            var sourceY = (layerY - offsetY) / fitScale;
            if (sourceX < 0 || sourceY < 0 ||
                sourceX >= bitmap.PixelWidth || sourceY >= bitmap.PixelHeight)
                return false;

            pixelX = Math.Clamp((int)Math.Floor(sourceX), 0, bitmap.PixelWidth - 1);
            pixelY = Math.Clamp((int)Math.Floor(sourceY), 0, bitmap.PixelHeight - 1);
            return true;
        }

        private void UpdateMainCoordinateReadout()
        {
            var center = new Point(XRayScanBorder.ActualWidth / 2.0, XRayScanBorder.ActualHeight / 2.0);
            MainCenterCoordinateText.Text = TryViewportToImagePixel(center, out var x, out var y)
                ? $"CENTER X: {x}  Y: {y}"
                : "CENTER X: —  Y: —";
        }

        private void UpdateMainPointerCoordinate(Point point)
        {
            MainPointerCoordinateText.Text = TryViewportToImagePixel(point, out var x, out var y)
                ? $"POINTER X: {x}  Y: {y}"
                : "POINTER X: —  Y: —";
        }

        private void FocusCoordinate_Click(object sender, RoutedEventArgs e)
        {
            if (XRayImage.Source is not BitmapSource bitmap)
            {
                MainCoordinateValidationText.Text = "Open an image first.";
                return;
            }

            if (!int.TryParse(MainCoordinateXInput.Text, out var pixelX) ||
                !int.TryParse(MainCoordinateYInput.Text, out var pixelY) ||
                !double.TryParse(MainCoordinateZoomInput.Text, out var zoomPercent) ||
                pixelX < 0 || pixelX >= bitmap.PixelWidth ||
                pixelY < 0 || pixelY >= bitmap.PixelHeight ||
                zoomPercent < 100)
            {
                MainCoordinateValidationText.Text =
                    $"X: 0–{bitmap.PixelWidth - 1}, Y: 0–{bitmap.PixelHeight - 1}, zoom ≥ 100";
                return;
            }

            var fitScale = Math.Min(
                ImageLayer.ActualWidth / bitmap.PixelWidth,
                ImageLayer.ActualHeight / bitmap.PixelHeight);
            var renderedWidth = bitmap.PixelWidth * fitScale;
            var renderedHeight = bitmap.PixelHeight * fitScale;
            var offsetX = (ImageLayer.ActualWidth - renderedWidth) / 2.0;
            var offsetY = (ImageLayer.ActualHeight - renderedHeight) / 2.0;
            var layerPointX = offsetX + (pixelX + 0.5) * fitScale;
            var layerPointY = offsetY + (pixelY + 0.5) * fitScale;
            _mainScale = Math.Clamp(zoomPercent / 100.0, MainMinZoom, MainNumericSafetyZoom);
            var originX = XRayScanBorder.ActualWidth / 2.0;
            var originY = XRayScanBorder.ActualHeight / 2.0;
            _mainTransX = -(layerPointX - originX) * _mainScale;
            _mainTransY = -(layerPointY - originY) * _mainScale;
            MainCoordinateValidationText.Text = string.Empty;
            SyncMainTransforms();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveLayout();
            ClampMainTranslation();
            SyncMainTransforms();
        }
        private void UpdateResponsiveLayout()
        {
            if (ContentGrid == null) return;
            bool compact = ActualWidth < 1050;
            bool medium = ActualWidth < 1250;
            TelemetryPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ContentGrid.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(medium ? 250 : 330);
            ContentGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            ContentGrid.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(medium ? 250 : 310);
            System.Windows.Controls.Grid.SetColumn(ScanPanel, 1);
            System.Windows.Controls.Grid.SetColumn(HistoryPanel, compact ? 1 : 2);
            System.Windows.Controls.Grid.SetRow(HistoryPanel, compact ? 1 : 0);
            ContentGrid.RowDefinitions[1].Height = compact ? new GridLength(220) : new GridLength(0);
            ScanPanel.Margin = compact ? new Thickness(0, 0, 0, 10) : new Thickness(10, 0, 0, 0);
            HistoryPanel.Margin = compact ? new Thickness(0) : new Thickness(10, 0, 0, 0);
        }


        /// <summary>
        /// History listesinden bir öğe seçildiğinde TIFF dosyasını diskten yükler ve ekrana basar.
        /// ViewModel'deki partial metot yerine bu event kullanılıyor – daha güvenilir.
        /// </summary>
        private void ImageHistoryListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ListBox lb) return;
            if (lb.SelectedItem is not XRaySystem.Application.DTOs.SensorDataDto item) return;

            var vm = DataContext as ViewModels.LiveTelemetryViewModel;
            if (vm == null) return;

            // Dosya yolu kontrolü
            if (string.IsNullOrEmpty(item.ImagePath) || !System.IO.File.Exists(item.ImagePath))
            {
                MessageBox.Show(
                    $"Image file not found.\nRequested path: {item.ImagePath ?? "(empty)"}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                vm.LoadHistoryTiff(item.ImagePath, item);
                vm.ImageStatusBadgeText  = $"HISTORY REVIEW: {item.Voltage:F0} kV · {item.PowerW:F0} W ({item.Timestamp:yyyy-MM-dd HH:mm:ss})";
                vm.ImageStatusBadgeColor = "#00F0FF";
                ResetImageView();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to load image:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Tüm TIFF görüntülerinin saklandığı ana klasörü (Explorer/Finder) açar.
        /// </summary>
        private void OpenImageFolder_Click(object sender, RoutedEventArgs e)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folderPath = System.IO.Path.Combine(appData, "XRaySystem", "Images");

            if (!System.IO.Directory.Exists(folderPath))
            {
                System.IO.Directory.CreateDirectory(folderPath);
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{folderPath}\"");
                }
                else if (OperatingSystem.IsMacOS())
                {
                    System.Diagnostics.Process.Start("open", $"\"{folderPath}\"");
                }
                else
                {
                    MessageBox.Show($"Image folder path:\n{folderPath}", "Folder Path");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open folder:\n{ex.Message}\n\nFolder path:\n{folderPath}", "Information");
            }
        }

        /// <summary>
        /// Seçilen TIFF dosyasını işletim sisteminin dosya gezgininde (Explorer/Finder) gösterir.
        /// </summary>
        private void OpenSingleFileInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not XRaySystem.Application.DTOs.SensorDataDto item) return;
            if (string.IsNullOrEmpty(item.ImagePath) || !System.IO.File.Exists(item.ImagePath))
            {
                MessageBox.Show("The file has not been saved yet or was deleted.", "Error");
                return;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.ImagePath}\"");
                }
                else if (OperatingSystem.IsMacOS())
                {
                    System.Diagnostics.Process.Start("open", $"-R \"{item.ImagePath}\"");
                }
                else
                {
                    MessageBox.Show($"File path:\n{item.ImagePath}", "File Path");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open file location:\n{ex.Message}", "Error");
            }
        }

        /// <summary>
        /// Seçilen görüntüyü kullanıcının seçeceği bir yere (Masaüstü, USB bellek vb.) kopyalar.
        /// </summary>
        private void ExportSingleFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not XRaySystem.Application.DTOs.SensorDataDto item) return;
            if (string.IsNullOrEmpty(item.ImagePath) || !System.IO.File.Exists(item.ImagePath))
            {
                MessageBox.Show("No file is available to export.", "Error");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"image_{item.Voltage:F0}kV_{item.PowerW:F0}W_{item.ExposureTime:F0}ms_{item.Timestamp:yyyyMMdd_HHmmss}.tiff",
                DefaultExt = ".tiff",
                Filter = "TIFF Image (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    System.IO.File.Copy(item.ImagePath, dialog.FileName, overwrite: true);
                    MessageBox.Show($"Image exported successfully:\n{dialog.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Unable to copy file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ════════════════════════════════════════════════
        //  COMPARISON HANDLERS
        // ════════════════════════════════════════════════

        private void AddToComparison_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.DataContext is not XRaySystem.Application.DTOs.SensorDataDto item) return;
            if (string.IsNullOrEmpty(item.ImagePath) || !File.Exists(item.ImagePath))
            {
                MessageBox.Show("The image file has not been saved yet.", "Information");
                return;
            }

            // Toggle: already selected? → remove
            var existing = _comparisonSelection.FirstOrDefault(c => c.Dto.Id == item.Id);
            if (existing != null)
            {
                _comparisonSelection.Remove(existing);
                SetCompBtnState(btn, false);
                RemoveThumbFromBar(item.Id);
            }
            else
            {
                if (_comparisonSelection.Count >= 4)
                {
                    MessageBox.Show("You can select up to 4 images.", "Selection Limit");
                    return;
                }

                // Load tiny thumbnail (300px wide, fast)
                BitmapSource? thumb = null;
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
                    bi.DecodePixelWidth = 220;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    thumb = bi;
                }
                catch { /* leave null */ }

                var ci = new ComparisonItem(item, thumb);
                _comparisonSelection.Add(ci);
                SetCompBtnState(btn, true);
                AddThumbToBar(ci);
                _compBtnMap[item.Id] = btn;
            }

            UpdateComparisonBarVisibility();
        }

        private void SetCompBtnState(Button btn, bool selected)
        {
            // Walk template to find TextBlock named BtnText
            if (btn.Template?.FindName("BtnText", btn) is System.Windows.Controls.TextBlock tb)
            {
                tb.Text = selected ? "✓" : "+";
                tb.Foreground = selected
                    ? new SolidColorBrush(Color.FromRgb(92, 255, 255))
                    : new SolidColorBrush(Color.FromRgb(0, 240, 255));
            }
            if (btn.Template?.FindName("Bd", btn) is Border bd)
            {
                bd.BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(0, 240, 255))
                    : new SolidColorBrush(Color.FromRgb(51, 65, 85));
                bd.Background = selected
                    ? new SolidColorBrush(Color.FromRgb(15, 23, 42))
                    : new SolidColorBrush(Color.FromRgb(30, 41, 59));
            }
        }

        private void AddThumbToBar(ComparisonItem ci)
        {
            var container = new Border
            {
                Width = 90, Height = 68,
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(5),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 100, 180)),
                BorderThickness = new Thickness(1),
                Background = Brushes.Black,
                ClipToBounds = true,
                Tag = ci.Dto.Id
            };
            var grid = new Grid();

            // Image
            var img = new Image
            {
                Source = ci.Thumbnail,
                Stretch = Stretch.Uniform,
                Opacity = 0.85
            };
            grid.Children.Add(img);

            // Info overlay
            var info = new TextBlock
            {
                Text = $"{ci.Dto.Timestamp:HH:mm:ss}\n{ci.Voltage:F0} kV · {ci.PowerW:F0} W",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 8,
                FontFamily = new FontFamily("Consolas"),
                Background = new SolidColorBrush(Color.FromArgb(160, 0, 10, 25)),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                TextWrapping = TextWrapping.Wrap
            };
            grid.Children.Add(info);

            // × remove button
            var removeBtn = new Button
            {
                Content = "✕",
                Width = 16, Height = 16,
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 0, 0)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 60, 60)),
                BorderThickness = new Thickness(0),
                FontSize = 9,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Tag = ci.Dto.Id
            };
            removeBtn.Click += RemoveFromComparison_Click;
            grid.Children.Add(removeBtn);

            container.Child = grid;
            ComparisonThumbStrip.Children.Add(container);
        }

        private void RemoveFromComparison_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not Guid id) return;
            var ci = _comparisonSelection.FirstOrDefault(c => c.Dto.Id == id);
            if (ci == null) return;
            _comparisonSelection.Remove(ci);
            RemoveThumbFromBar(id);
            // Reset + button if still in DOM
            if (_compBtnMap.TryGetValue(id, out var btn))
            {
                SetCompBtnState(btn, false);
                _compBtnMap.Remove(id);
            }
            UpdateComparisonBarVisibility();
        }

        private void RemoveThumbFromBar(Guid id)
        {
            var toRemove = ComparisonThumbStrip.Children
                .OfType<Border>()
                .FirstOrDefault(b => b.Tag is Guid g && g == id);
            if (toRemove != null) ComparisonThumbStrip.Children.Remove(toRemove);
        }

        private void ClearComparison_Click(object sender, RoutedEventArgs e)
        {
            // Reset all + buttons
            foreach (var ci in _comparisonSelection.ToList())
            {
                if (_compBtnMap.TryGetValue(ci.Dto.Id, out var btn))
                    SetCompBtnState(btn, false);
            }
            _comparisonSelection.Clear();
            _compBtnMap.Clear();
            ComparisonThumbStrip.Children.Clear();
            UpdateComparisonBarVisibility();
        }

        private void UpdateComparisonBarVisibility()
        {
            ComparisonBar.Visibility = _comparisonSelection.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OpenComparison_Click(object sender, RoutedEventArgs e)
        {
            if (_comparisonSelection.Count == 0) return;
            var win = new ComparisonWindow(_comparisonSelection);
            win.Owner = this;
            win.ShowDialog();
        }

    }
}
