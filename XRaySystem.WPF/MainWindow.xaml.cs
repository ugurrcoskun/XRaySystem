using System.Windows;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows.Input;
using XRaySystem.WPF.ViewModels;

namespace XRaySystem.WPF
{
    public partial class MainWindow : Window
    {
        private bool _startupHistoryLoadStarted;

        // DI üzerinden LiveTelemetryViewModel otomatik olarak enjekte edilecek
        public MainWindow(LiveTelemetryViewModel viewModel)
        {
            InitializeComponent();

            DataContext = viewModel;

            Loaded += MainWindow_Loaded;
            SizeChanged += MainWindow_SizeChanged;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
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

        private const double MinZoom = 1.0;
        private const double MaxZoom = 20.0;
        private Point _start;
        private Point _origin;
        private bool _panStarted;

        private void XRayImage_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var border = (System.Windows.Controls.Border)sender;
            Point mousePos = e.GetPosition(border);

            // Mac Trackpad (Parallels vb.) için hassas ve pürüzsüz zoom hesaplaması
            // Standart fare tekerleği e.Delta genelde 120'dir. Trackpad'ler daha küçük ve sık değer atar.
            if (e.Delta == 0) return;
            ApplyZoom(Math.Exp(e.Delta / 1200.0), mousePos);
            e.Handled = true;
        }

        private void ApplyZoom(double zoomFactor, Point center)
        {
            double oldScale = XRayScaleTransform.ScaleX;
            double newScale = Math.Clamp(oldScale * zoomFactor, MinZoom, MaxZoom);
            if (Math.Abs(newScale - oldScale) < 0.000001) return;
            var rect = GetImageRect();
            var transformOrigin = GetTransformOrigin();
            var imagePoint = new Point((center.X - transformOrigin.X - XRayTranslateTransform.X) / oldScale + transformOrigin.X,
                (center.Y - transformOrigin.Y - XRayTranslateTransform.Y) / oldScale + transformOrigin.Y);
            XRayScaleTransform.ScaleX = XRayScaleTransform.ScaleY = newScale;
            XRayTranslateTransform.X = center.X - transformOrigin.X - (imagePoint.X - transformOrigin.X) * newScale;
            XRayTranslateTransform.Y = center.Y - transformOrigin.Y - (imagePoint.Y - transformOrigin.Y) * newScale;
            ClampTranslation();
            UpdateZoomText();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            var center = new Point(XRayContainer.ActualWidth / 2, XRayContainer.ActualHeight / 2);
            ApplyZoom(1.25, center);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            var center = new Point(XRayContainer.ActualWidth / 2, XRayContainer.ActualHeight / 2);
            ApplyZoom(1 / 1.25, center);
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e)
        {
            XRayScaleTransform.ScaleX = XRayScaleTransform.ScaleY = MinZoom;
            XRayTranslateTransform.X = XRayTranslateTransform.Y = 0;
            UpdateZoomText();
        }

        private void XRayImage_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var border = (System.Windows.Controls.Border)sender;
            if (XRayScaleTransform.ScaleX > 1.0) // Sadece zoom yapıldıysa kaydırmaya izin ver
            {
                border.CaptureMouse();
                _start = e.GetPosition(border);
                _origin = new Point(XRayTranslateTransform.X, XRayTranslateTransform.Y);
                _panStarted = false;
            }
        }

        private void XRayImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var border = (System.Windows.Controls.Border)sender;
            if (border.IsMouseCaptured)
            {
                var current = e.GetPosition(border);
                var delta = current - _start;
                if (!_panStarted && delta.Length < 4) return;
                _panStarted = true;
                XRayTranslateTransform.X = _origin.X + delta.X;
                XRayTranslateTransform.Y = _origin.Y + delta.Y;
                ClampTranslation();
            }
        }

        private void XRayImage_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var border = (System.Windows.Controls.Border)sender;
            border.ReleaseMouseCapture();
            _panStarted = false;
        }

        private void XRayImage_LostMouseCapture(object sender, MouseEventArgs e) => _panStarted = false;

        private Rect GetImageRect()
        {
            if (XRayImage.Source is not System.Windows.Media.Imaging.BitmapSource source || XRayContainer.ActualWidth <= 0 || XRayContainer.ActualHeight <= 0)
                return new Rect(0, 0, XRayContainer.ActualWidth, XRayContainer.ActualHeight);
            double fit = Math.Min(XRayContainer.ActualWidth / source.PixelWidth, XRayContainer.ActualHeight / source.PixelHeight);
            double width = source.PixelWidth * fit, height = source.PixelHeight * fit;
            return new Rect((XRayContainer.ActualWidth - width) / 2, (XRayContainer.ActualHeight - height) / 2, width, height);
        }

        private void ClampTranslation()
        {
            var rect = GetImageRect();
            var origin = GetTransformOrigin();
            XRayTranslateTransform.X = ClampAxis(XRayTranslateTransform.X, rect.Left, rect.Right, origin.X, XRayScaleTransform.ScaleX, XRayContainer.ActualWidth);
            XRayTranslateTransform.Y = ClampAxis(XRayTranslateTransform.Y, rect.Top, rect.Bottom, origin.Y, XRayScaleTransform.ScaleY, XRayContainer.ActualHeight);
        }

        private static double ClampAxis(double value, double contentStart, double contentEnd, double origin, double scale, double viewport)
        {
            double start = origin + (contentStart - origin) * scale;
            double end = origin + (contentEnd - origin) * scale;
            if (end - start <= viewport)
                return origin - (start + end) / 2;

            double min = viewport - end;
            double max = -start;
            return Math.Clamp(value, min, max);
        }

        private Point GetTransformOrigin() => new(XRayContainer.ActualWidth / 2, XRayContainer.ActualHeight / 2);

        private void UpdateZoomText() => ZoomPercentText.Text = $"{XRayScaleTransform.ScaleX * 100:0}%";
        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) { ClampTranslation(); UpdateResponsiveLayout(); }
        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LiveTelemetryViewModel.CurrentFrame))
                Dispatcher.BeginInvoke(new Action(UpdateResponsiveLayout));
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
            System.Windows.Controls.Grid.SetColumn(ScanPanel, compact ? 0 : 1);
            System.Windows.Controls.Grid.SetColumn(HistoryPanel, compact ? 0 : 2);
            System.Windows.Controls.Grid.SetRow(HistoryPanel, compact ? 1 : 0);
            ContentGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        }

        private void ResetImageView()
        {
            XRayScaleTransform.ScaleX = XRayScaleTransform.ScaleY = MinZoom;
            XRayTranslateTransform.X = XRayTranslateTransform.Y = 0;
            UpdateZoomText();
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
                    $"Görüntü dosyası bulunamadı.\nAranan yol: {item.ImagePath ?? "(boş)"}",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                // TIFF dosyasını MemoryStream üzerinden yükle (path encoding sorunlarını önler)
                byte[] fileBytes = System.IO.File.ReadAllBytes(item.ImagePath);
                using var ms = new System.IO.MemoryStream(fileBytes);

                var decoder = new System.Windows.Media.Imaging.TiffBitmapDecoder(
                    ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                var frame = decoder.Frames[0];
                frame.Freeze();

                // CurrentFrame'i güncelle, live yayını duraklat ve rozeti güncelle
                vm.IsPaused              = true;
                vm.CurrentFrame          = frame;
                vm.ImageStatusBadgeText  = $"📁 GEÇMİŞ İNCELEME: {System.IO.Path.GetFileName(item.ImagePath)} ({item.Timestamp:yyyy-MM-dd HH:mm:ss})";
                vm.ImageStatusBadgeColor = "#00F0FF";
                ResetImageView();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Görüntü yüklenemedi:\n{ex.Message}",
                    "Hata",
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
                    MessageBox.Show($"Görüntü Klasörü Yolu:\n{folderPath}", "Klasör Yolu");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Klasör açılamadı:\n{ex.Message}\n\nKlasör Yolu:\n{folderPath}", "Bilgi");
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
                MessageBox.Show("Dosya henüz kaydedilmemiş veya silinmiş.", "Hata");
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
                    MessageBox.Show($"Dosya Yolu:\n{item.ImagePath}", "Dosya Yolu");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Dosya konumu açılamadı:\n{ex.Message}", "Hata");
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
                MessageBox.Show("Dışa aktarılacak dosya bulunamadı.", "Hata");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = System.IO.Path.GetFileName(item.ImagePath),
                DefaultExt = ".tiff",
                Filter = "TIFF Görüntü Dosyası (*.tiff;*.tif)|*.tiff;*.tif|Tüm Dosyalar (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    System.IO.File.Copy(item.ImagePath, dialog.FileName, overwrite: true);
                    MessageBox.Show($"Görüntü başarıyla aktarıldı:\n{dialog.FileName}", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Dosya kopyalanamadı:\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

    }
}
