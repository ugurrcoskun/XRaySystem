using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XRaySystem.Application.DTOs;
using XRaySystem.Application.UseCases;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Infrastructure.Sensors;
using XRaySystem.WPF.Services;

namespace XRaySystem.WPF.ViewModels;

public partial class LiveTelemetryViewModel : ObservableObject
{
    private readonly ISensorService _sensorService;
    private readonly StartCaptureUseCase _startCaptureUseCase;
    private readonly ProcessTelemetryUseCase _processTelemetryUseCase;
    private readonly SaveMeasurementUseCase _saveMeasurementUseCase;
    private readonly GetMeasurementHistoryUseCase _getHistoryUseCase;
    private readonly SnapMeasurementUseCase _snapMeasurementUseCase;
    private readonly ResumeLiveUseCase _resumeLiveUseCase;
    private readonly IImageStorageService _imageStorageService;
    private readonly SemaphoreSlim _historyLoadLock = new(1, 1);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectAndStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    private string _deviceStatus = XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectAndStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isCapturing = false;

    [ObservableProperty]
    private SensorDataDto? _latestTelemetry;

    [ObservableProperty]
    private System.Windows.Media.ImageSource? _currentFrame;

    [ObservableProperty]
    private string _imageStatusBadgeText = "🔴 CANLI YAYIN (512x512 Preview - Akış Aktif)";

    [ObservableProperty]
    private string _imageStatusBadgeColor = "#34C759";

    public ObservableCollection<SensorDataDto> TelemetryHistory { get; } = new();
    
    // Snap ile kaydedilen görüntülerin geçmişi
    public ObservableCollection<SensorDataDto> ImageHistory { get; } = new();

    [ObservableProperty]
    private SensorDataDto? _selectedHistoryItem;

    private XRayMeasurement? _lastRawMeasurement;

    public LiveTelemetryViewModel(
        ISensorService sensorService,
        StartCaptureUseCase startCaptureUseCase,
        ProcessTelemetryUseCase processTelemetryUseCase,
        SaveMeasurementUseCase saveMeasurementUseCase,
        GetMeasurementHistoryUseCase getHistoryUseCase,
        SnapMeasurementUseCase snapMeasurementUseCase,
        ResumeLiveUseCase resumeLiveUseCase,
        IImageStorageService imageStorageService)
    {
        _sensorService = sensorService;
        _startCaptureUseCase = startCaptureUseCase;
        _processTelemetryUseCase = processTelemetryUseCase;
        _saveMeasurementUseCase = saveMeasurementUseCase;
        _getHistoryUseCase = getHistoryUseCase;
        _snapMeasurementUseCase = snapMeasurementUseCase;
        _resumeLiveUseCase = resumeLiveUseCase;
        _imageStorageService = imageStorageService;

        _sensorService.StatusChanged += OnDeviceStatusChanged;
        _sensorService.MeasurementReceived += measurement => OnMeasurementReceived(measurement);
        
    }

    private void OnDeviceStatusChanged(DeviceStatus status)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            DeviceStatus = status.ToString();
            IsCapturing = status == XRaySystem.Domain.Entities.DeviceStatus.Capturing;
        });
    }

    private void OnMeasurementReceived(XRayMeasurement measurement, bool displayWhilePaused = false)
    {
        _lastRawMeasurement = measurement;

        // DispatcherPriority.Background: UI'ın kullanıcı etkileşimlerine öncelik vermesini sağlar
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () =>
        {
            var data = _processTelemetryUseCase.Execute(measurement);
            LatestTelemetry = data;
            
            TelemetryHistory.Add(data);
            if (TelemetryHistory.Count > 50)
                TelemetryHistory.RemoveAt(0); 

            if (data.ImageMatrix != null && data.ImageMatrix.Length > 0)
            {
                try
                {
                    // Genişlik/yüksekliği array boyutundan otomatik hesapla
                    // (live: 512×512, snap: 2324×2930)
                    int total  = data.ImageMatrix.Length;
                    int width  = (int)Math.Sqrt(total); // kare değilse yaklaşık
                    int height = total / width;

                    // Snap görüntüsü ise tam boyutları kullan
                    if (total == MockXRaySensorService.Width * MockXRaySensorService.Height)
                    {
                        width  = MockXRaySensorService.Width;
                        height = MockXRaySensorService.Height;
                    }
                    else if (total == MockXRaySensorService.LiveWidth * MockXRaySensorService.LiveHeight)
                    {
                        width  = MockXRaySensorService.LiveWidth;
                        height = MockXRaySensorService.LiveHeight;
                    }

                    var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                        width, height, 96, 96, 
                        System.Windows.Media.PixelFormats.Gray16, 
                        null, 
                        data.ImageMatrix, 
                        width * 2);

                    bitmap.Freeze();
                     if (!IsPaused || displayWhilePaused)
                     {
                         CurrentFrame = bitmap;
                     }
                }
                catch (Exception)
                {
                    // Görüntü render edilemezse yoksay
                }
            }

            // Exposure limitleri telemetri değişince güncellenir
            OnPropertyChanged(nameof(MaxExposureTime));
            OnPropertyChanged(nameof(ExposureTimeIsValid));
            OnPropertyChanged(nameof(ExposureValidationMessage));
        });
    }

    private bool CanConnectAndStart() => !IsCapturing;
    private bool CanStop() => IsCapturing;
    private bool CanDisconnect() => DeviceStatus != XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();
    private bool CanSnap() => DeviceStatus != XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();

    [ObservableProperty]
    private bool _isSnapping = false;

    [ObservableProperty]
    private bool _isConnecting = false;

    [ObservableProperty]
    private bool _isPaused = false;

    /// <summary>
    /// Kullanıcının girdiği exposure süresi (ms).
    /// Değiştirildiğinde otomatik olarak geçerli aralığa sıkıştırılır.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureTimeIsValid))]
    [NotifyPropertyChangedFor(nameof(ExposureValidationMessage))]
    private double _userExposureTime = 500.0;

    /// <summary>Min exposure süresi: her zaman 1ms (deteöktör sınırı)</summary>
    public double MinExposureTime => 1.0;

    /// <summary>
    /// Max exposure süresi: kV ve mA'ya bağlı hesaplanır.
    /// yüksek kV = daha az süre gerekli, yüksek mA = daha az süre gerekli.
    /// Formül: MaxmAs = 150 - (kV - 40) * 0.8  (kV arttıkça mAs payı azalır)
    ///          MaxExposure = MaxmAs / mA * 1000
    ///          [1ms, 3000ms] arasında sınırlandırılır.
    /// </summary>
    public double MaxExposureTime
    {
        get
        {
            double kv  = LatestTelemetry?.Voltage     ?? 70.0;
            double mA  = LatestTelemetry?.TubeCurrent ?? 50.0;
            double maxMAs = Math.Max(10, 150.0 - (kv - 40.0) * 0.8);
            double maxMs  = (maxMAs / mA) * 1000.0;
            return Math.Clamp(maxMs, 1.0, 3000.0);
        }
    }

    public bool   ExposureTimeIsValid      => UserExposureTime >= MinExposureTime && UserExposureTime <= MaxExposureTime;
    public string ExposureValidationMessage => ExposureTimeIsValid
        ? $"Geçerli aralık: {MinExposureTime:F0}–{MaxExposureTime:F0} ms"
        : $"⚠ Geçersiz! Aralık: {MinExposureTime:F0}–{MaxExposureTime:F0} ms";

    [RelayCommand(CanExecute = nameof(CanSnap))]
    private async Task SnapAsync()
    {
        try
        {
            IsSnapping = true;

            // Adım 1: Sensörden görüntü al (exposure time kadar bekler)
            var measurement = await _snapMeasurementUseCase.ExecuteAsync(UserExposureTime);

            // Adım 2: Dosya adını oluştur (image_Voltage_Current_Exposure_Date.tiff)
            var fileName = $"image_{measurement.Readings.Voltage:F0}kV" +
                           $"_{measurement.Readings.TubeCurrent:F0}mA" +
                           $"_{measurement.Readings.ExposureTime:F0}ms" +
                           $"_{DateTime.Now:yyyyMMdd_HHmmss}.tiff";

            // Adım 3a: BitmapSource'u UI (STA) thread'de oluştur ve dondur
            var snapBitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                MockXRaySensorService.Width,
                MockXRaySensorService.Height,
                96, 96,
                System.Windows.Media.PixelFormats.Gray16,
                null,
                measurement.ImageMatrix!,
                MockXRaySensorService.Width * 2);
            snapBitmap.Freeze(); // Dondurulmuş bitmap her thread'den erişilebilir

            // Adım 3b: TIFF dosyasını background thread'de kaydet (yalnızca dosya I/O)
            string imagePath = await Task.Run(() =>
                _imageStorageService.SaveTiff(snapBitmap, fileName));

            // Adım 4: Dosya yolunu entity'e yaz
            measurement.SetImagePath(imagePath);

            // Adım 5: Veritabanına kaydet (Repository Use Case kullanarak)
            await _saveMeasurementUseCase.ExecuteAsync(measurement);

            // Adım 6: UI'ı güncelle – görüntüyü göster ve History'e ekle
            IsPaused = true;
            OnMeasurementReceived(measurement, displayWhilePaused: true);

            ImageStatusBadgeText  = $"📸 SNAP DONDURULDU ({MockXRaySensorService.Width}x{MockXRaySensorService.Height} TIFF - {Path.GetFileName(imagePath)})";
            ImageStatusBadgeColor = "#FF9500";

            var dto = new SensorDataDto
            {
                Id = measurement.Id,
                Timestamp = measurement.Timestamp.ToLocalTime(),
                TubeCurrent = measurement.Readings.TubeCurrent,
                Voltage = measurement.Readings.Voltage,
                XRayIntensity = measurement.Readings.XRayIntensity,
                ExposureTime = measurement.Readings.ExposureTime,
                ImagePath = imagePath
            };

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                ImageHistory.Insert(0, dto)); // En yeni üstte
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Snap Hatası");
        }
        finally
        {
            IsSnapping = false;
        }
    }

    [RelayCommand]
    private void LoadFromHistory(SensorDataDto? item)
    {
        if (item?.ImagePath == null || !File.Exists(item.ImagePath))
        {
            System.Windows.MessageBox.Show("Görüntü dosyası bulunamadı.", "Hata");
            return;
        }

        try
        {
            var bitmapSource = _imageStorageService.LoadTiff(item.ImagePath);
            IsPaused = true; // Canlı yayını durdur
            CurrentFrame = bitmapSource;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Görüntü yüklenemedi: {ex.Message}", "Hata");
        }
    }

    [RelayCommand]
    private async Task ResumeLiveAsync()
    {
        IsPaused = false;
        ImageStatusBadgeText  = "🔴 CANLI YAYIN (512x512 Preview - Akış Aktif)";
        ImageStatusBadgeColor = "#34C759";
        await _resumeLiveUseCase.ExecuteAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConnectAndStart))]
    private async Task ConnectAndStartAsync()
    {
        try
        {
            IsConnecting = true;
            await _startCaptureUseCase.ExecuteAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Sensör Başlatma Hatası");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await _sensorService.StopCaptureAsync();
    }
    
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await _sensorService.DisconnectAsync();
    }

    [RelayCommand]
    private async Task SaveLatestAsync()
    {
        // Snap artık otomatik DB'ye kaydediyor.
        // Bu buton artık kullanım dışı; tekrar kaydetmeye çalışmak
        // EF Core tracked-entity duplicate hatası verir.
        System.Windows.MessageBox.Show(
            "Snap yapıldığında görüntü otomatik olarak kaydedilir.\nTekrar kaydetmeye gerek yok.",
            "Bilgi",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        await _historyLoadLock.WaitAsync();
        try
        {
            var history = await _getHistoryUseCase.ExecuteAsync();
            
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ImageHistory.Clear();
                foreach (var item in history)
                {
                    // Diskte resmi kaydedilmiş tüm çekimleri ImageHistory listesine ekle
                    if (!string.IsNullOrEmpty(item.ImagePath))
                    {
                        ImageHistory.Add(item);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshHistoryAsync Hata: {ex.Message}");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                System.Windows.MessageBox.Show(
                    $"Geçmiş kayıtları yüklenemedi:\n{ex.Message}",
                    "Geçmiş Yükleme Hatası",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning));
        }
        finally
        {
            _historyLoadLock.Release();
        }
    }
}
