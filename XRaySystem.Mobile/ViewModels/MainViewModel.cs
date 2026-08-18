using System.IO;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using XRaySystem.Application.UseCases;
using XRaySystem.Application.DTOs;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Domain.WindowLevel;
using XRaySystem.Mobile.Services;

namespace XRaySystem.Mobile.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISensorService _sensorService;
    private readonly SnapMeasurementUseCase _snapMeasurement;
    private readonly SaveMeasurementUseCase _saveMeasurement;
    private readonly GetMeasurementHistoryUseCase _getHistory;
    private readonly MauiImageStorageService _imageStorage;
    private readonly MauiWindowLevelRenderer _renderer;

    private CancellationTokenSource? _snapCts;
    private RawGray16Frame? _currentRawFrame;
    private RenderRequest? _pendingRender;
    private int _renderWorkerRunning;
    private int _suppressWindowRender;
    private long _renderVersion;
    private bool _historyInitialized;

    private sealed record RenderRequest(RawGray16Frame Frame, int Center, int Width, long Version);

    public IReadOnlyList<string> ProfileOptions { get; } =
        ["Endüstriyel QA Fantomu", "Medikal Numune Fantomu"];

    public ObservableCollection<SensorDataDto> RecentImages { get; } = [];

    [ObservableProperty]
    private DeviceStatus _status = DeviceStatus.Disconnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartLiveCommand))]
    [NotifyPropertyChangedFor(nameof(AcquisitionParametersAreValid))]
    [NotifyPropertyChangedFor(nameof(ParametersValidationMessage))]
    [NotifyPropertyChangedFor(nameof(ParametersAccentColor))]
    private double _kv = 70.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartLiveCommand))]
    [NotifyPropertyChangedFor(nameof(AcquisitionParametersAreValid))]
    [NotifyPropertyChangedFor(nameof(ParametersValidationMessage))]
    [NotifyPropertyChangedFor(nameof(ParametersAccentColor))]
    private double _watt = 3500.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartLiveCommand))]
    [NotifyPropertyChangedFor(nameof(AcquisitionParametersAreValid))]
    [NotifyPropertyChangedFor(nameof(ParametersValidationMessage))]
    [NotifyPropertyChangedFor(nameof(ParametersAccentColor))]
    private double _exposureTime = 1000.0;

    [ObservableProperty]
    private int _selectedProfileIndex;

    [ObservableProperty]
    private ImageSource? _currentImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCurrentImage))]
    private bool _hasCurrentImage;

    [ObservableProperty]
    private string _statusMessage = "Sistem hazır. Cihaz bağlantısı bekleniyor.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isHistoryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRecentImages))]
    private bool _hasRecentImages;

    [ObservableProperty]
    private double _measuredPowerW;

    [ObservableProperty]
    private double _measuredVoltageKv;

    [ObservableProperty]
    private double _measuredExposureMs;

    [ObservableProperty]
    private double _measuredIntensity;

    [ObservableProperty]
    private string _frameKindDisplay = "STANDBY";

    [ObservableProperty]
    private string _frameMetadataText = "NO FRAME · 16-BIT";

    [ObservableProperty]
    private string _lastFrameTimeText = "Henüz görüntü alınmadı";

    [ObservableProperty]
    private double _windowCenter = 32767.5;

    [ObservableProperty]
    private double _windowWidth = 65535;

    public bool HasNoCurrentImage => !HasCurrentImage;
    public bool HasNoRecentImages => !HasRecentImages;

    public bool AcquisitionParametersAreValid =>
        new AcquisitionParameters(Kv, Watt, ExposureTime).IsValid;

    public string ParametersValidationMessage => AcquisitionParametersAreValid
        ? $"Hazır · {Kv:F0} kV · {Watt:F0} W · {ExposureTime:F0} ms"
        : "kV ve Watt sıfırdan büyük, süre 1–3000 ms aralığında olmalıdır.";

    public string ParametersAccentColor => AcquisitionParametersAreValid ? "#32D74B" : "#FF4D5E";

    public string StatusDisplayText => Status switch
    {
        DeviceStatus.Connected => "HAZIR",
        DeviceStatus.Capturing => "CANLI",
        DeviceStatus.Snapping => "ÇEKİM",
        DeviceStatus.Frozen => "DONDURULDU",
        DeviceStatus.Error => "HATA",
        _ => "BAĞLANTI YOK"
    };

    public string StatusAccentColor => Status switch
    {
        DeviceStatus.Connected => "#32D74B",
        DeviceStatus.Capturing => "#00E5FF",
        DeviceStatus.Snapping => "#FFB020",
        DeviceStatus.Frozen => "#FFB020",
        DeviceStatus.Error => "#FF4D5E",
        _ => "#667991"
    };

    public string StatusBadgeBackground => Status switch
    {
        DeviceStatus.Connected => "#163A27",
        DeviceStatus.Capturing => "#123844",
        DeviceStatus.Snapping => "#3C2A13",
        DeviceStatus.Frozen => "#3C2A13",
        DeviceStatus.Error => "#351720",
        _ => "#182131"
    };

    public MainViewModel(
        ISensorService sensorService,
        SnapMeasurementUseCase snapMeasurement,
        SaveMeasurementUseCase saveMeasurement,
        GetMeasurementHistoryUseCase getHistory,
        MauiImageStorageService imageStorage,
        MauiWindowLevelRenderer renderer)
    {
        _sensorService = sensorService;
        _snapMeasurement = snapMeasurement;
        _saveMeasurement = saveMeasurement;
        _getHistory = getHistory;
        _imageStorage = imageStorage;
        _renderer = renderer;

        _sensorService.StatusChanged += status =>
            MainThread.BeginInvokeOnMainThread(() => Status = status);

        _sensorService.MeasurementReceived += measurement => AcceptMeasurement(measurement, queueRender: true);
    }

    private bool CanConnect() => !IsBusy && Status == DeviceStatus.Disconnected;
    private bool CanDisconnect() => !IsBusy && Status != DeviceStatus.Disconnected;
    private bool CanStartLive() =>
        !IsBusy &&
        (Status is DeviceStatus.Connected or DeviceStatus.Frozen) &&
        AcquisitionParametersAreValid;
    private bool CanStopLive() => Status is DeviceStatus.Capturing or DeviceStatus.Snapping;
    private bool CanSnap() =>
        !IsBusy &&
        (Status is DeviceStatus.Connected or DeviceStatus.Frozen) &&
        AcquisitionParametersAreValid;
    private bool CanAdjustImage() => Volatile.Read(ref _currentRawFrame) is not null;

    private void ApplyAcquisitionParameters()
    {
        var parameters = new AcquisitionParameters(Kv, Watt, ExposureTime);
        parameters.EnsureValid();
        _sensorService.Parameters = parameters;
        _sensorService.Profile = SelectedProfileIndex == 1
            ? AcquisitionProfile.MedicalSpecimenLikePhantom
            : AcquisitionProfile.IndustrialQaPhantom;
    }

    partial void OnStatusChanged(DeviceStatus value)
    {
        OnPropertyChanged(nameof(StatusDisplayText));
        OnPropertyChanged(nameof(StatusAccentColor));
        OnPropertyChanged(nameof(StatusBadgeBackground));
        NotifyCommandStates();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();

    partial void OnSelectedProfileIndexChanged(int value)
    {
        _sensorService.Profile = value == 1
            ? AcquisitionProfile.MedicalSpecimenLikePhantom
            : AcquisitionProfile.IndustrialQaPhantom;
    }

    partial void OnWindowCenterChanged(double value) => QueueCurrentFrameForRender();
    partial void OnWindowWidthChanged(double value) => QueueCurrentFrameForRender();

    private void NotifyCommandStates()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        StartLiveCommand.NotifyCanExecuteChanged();
        StopLiveCommand.NotifyCanExecuteChanged();
        SnapCommand.NotifyCanExecuteChanged();
        AutoWindowLevelCommand.NotifyCanExecuteChanged();
        ResetWindowLevelCommand.NotifyCanExecuteChanged();
    }

    public async Task InitializeAsync()
    {
        if (_historyInitialized) return;
        _historyInitialized = true;
        await RefreshHistoryAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        StatusMessage = "Sensör bağlantısı kuruluyor...";
        try
        {
            await _sensorService.ConnectAsync();
            StatusMessage = "Sensör bağlı. Parametreler uygulanmaya hazır.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Bağlantı hatası: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        StatusMessage = "Sensör güvenli şekilde ayrılıyor...";
        _snapCts?.Cancel();
        try
        {
            await _sensorService.DisconnectAsync();
            StatusMessage = "Bağlantı kesildi. Son görüntü ekranda korunuyor.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Bağlantı kesme hatası: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartLive))]
    private async Task StartLiveAsync()
    {
        IsBusy = true;
        StatusMessage = "Canlı Gray16 akışı hazırlanıyor...";
        try
        {
            ApplyAcquisitionParameters();
            await _sensorService.StartCaptureAsync();
            StatusMessage = "Canlı akış aktif · latest-frame modu açık.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Canlı akış hatası: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopLive))]
    private async Task StopLiveAsync()
    {
        StatusMessage = "Akış durduruluyor...";
        _snapCts?.Cancel();
        try
        {
            await _sensorService.StopCaptureAsync();
            StatusMessage = "Akış durduruldu. Son görüntü korunuyor.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Akış durdurma hatası: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSnap))]
    private async Task SnapAsync()
    {
        IsBusy = true;
        StatusMessage = "Yüksek çözünürlüklü snap alınıyor...";
        _snapCts?.Dispose();
        _snapCts = new CancellationTokenSource();

        try
        {
            ApplyAcquisitionParameters();
            var measurement = await _snapMeasurement.ExecuteAsync(ExposureTime, _snapCts.Token);
            if (measurement.Frame is null) return;

            AcceptMeasurement(measurement, queueRender: false);
            var center = (int)Math.Round(WindowCenter);
            var width = Math.Max(1, (int)Math.Round(WindowWidth));
            var bmpBytes = await Task.Run(() =>
                _renderer.RenderToBmp(measurement.Frame, center, width), _snapCts.Token);
            SetCurrentImage(bmpBytes);

            var measuredPowerW = measurement.Readings.Voltage * measurement.Readings.TubeCurrent;
            var fileName = $"Snap_{measurement.Readings.Voltage:F0}kV_{measuredPowerW:F0}W_" +
                           $"{measurement.Readings.ExposureTime:F0}ms_{DateTime.Now:yyyyMMdd_HHmmss}.bmp";
            var path = await Task.Run(() => _imageStorage.SaveBmp(bmpBytes, fileName), _snapCts.Token);

            measurement.SetImagePath(path);
            await _saveMeasurement.ExecuteAsync(measurement);
            await RefreshHistoryAsync();
            StatusMessage = $"Snap kaydedildi · {fileName}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Çekim kullanıcı veya yaşam döngüsü tarafından iptal edildi.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Snap hatası: {ex.Message}";
        }
        finally
        {
            _snapCts?.Dispose();
            _snapCts = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ResetParameters()
    {
        Kv = 70;
        Watt = 3500;
        ExposureTime = 1000;
        SelectedProfileIndex = 0;
        StatusMessage = "Acquisition parametreleri varsayılan değerlere döndürüldü.";
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        if (IsHistoryLoading) return;
        IsHistoryLoading = true;
        try
        {
            var items = (await _getHistory.ExecuteAsync())
                .Where(item => !string.IsNullOrWhiteSpace(item.ImagePath) && File.Exists(item.ImagePath))
                .Take(12)
                .ToList();

            RecentImages.Clear();
            foreach (var item in items) RecentImages.Add(item);
            HasRecentImages = RecentImages.Count > 0;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Geçmiş yüklenemedi: {ex.Message}";
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadHistoryImageAsync(SensorDataDto? item)
    {
        if (item?.ImagePath is null) return;
        try
        {
            var bytes = await Task.Run(() => _imageStorage.LoadBmp(item.ImagePath));
            Volatile.Write(ref _currentRawFrame, null);
            Interlocked.Increment(ref _renderVersion);
            Interlocked.Exchange(ref _pendingRender, null);
            SetCurrentImage(bytes);
            MeasuredPowerW = item.PowerW;
            MeasuredVoltageKv = item.Voltage;
            MeasuredExposureMs = item.ExposureTime;
            MeasuredIntensity = item.XRayIntensity;
            FrameKindDisplay = "HISTORY · BMP";
            FrameMetadataText = "8-BIT PREVIEW · LOCAL";
            LastFrameTimeText = item.Timestamp.ToString("dd.MM.yyyy · HH:mm:ss");
            AutoWindowLevelCommand.NotifyCanExecuteChanged();
            ResetWindowLevelCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Geçmiş görüntüsü açıldı · {Path.GetFileName(item.ImagePath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Geçmiş görüntüsü açılamadı: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdjustImage))]
    private async Task AutoWindowLevelAsync()
    {
        var frame = Volatile.Read(ref _currentRawFrame);
        if (frame is null) return;

        var result = await Task.Run(() => WindowLevelMapper.Auto(frame.Pixels));
        if (!ReferenceEquals(frame, Volatile.Read(ref _currentRawFrame))) return;

        Interlocked.Exchange(ref _suppressWindowRender, 1);
        try
        {
            WindowCenter = result.Center;
            WindowWidth = result.Width;
        }
        finally
        {
            Interlocked.Exchange(ref _suppressWindowRender, 0);
        }
        QueueLatestRender(frame);
        StatusMessage = "Otomatik Window/Level görüntü histogramından hesaplandı.";
    }

    [RelayCommand(CanExecute = nameof(CanAdjustImage))]
    private void ResetWindowLevel()
    {
        var frame = Volatile.Read(ref _currentRawFrame);
        if (frame is null) return;

        Interlocked.Exchange(ref _suppressWindowRender, 1);
        try
        {
            WindowCenter = 32767.5;
            WindowWidth = 65535;
        }
        finally
        {
            Interlocked.Exchange(ref _suppressWindowRender, 0);
        }
        QueueLatestRender(frame);
        StatusMessage = "Window/Level tam Gray16 aralığına sıfırlandı.";
    }

    private void AcceptMeasurement(XRayMeasurement measurement, bool queueRender)
    {
        var frame = measurement.Frame;
        if (frame is null) return;

        Volatile.Write(ref _currentRawFrame, frame);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            MeasuredPowerW = measurement.Readings.Voltage * measurement.Readings.TubeCurrent;
            MeasuredVoltageKv = measurement.Readings.Voltage;
            MeasuredExposureMs = measurement.Readings.ExposureTime;
            MeasuredIntensity = measurement.Readings.XRayIntensity;
            FrameKindDisplay = frame.Kind == FrameKind.LivePreview ? "LIVE PREVIEW" : "SNAP · FROZEN";
            FrameMetadataText = $"{frame.Width} × {frame.Height} · GRAY16";
            LastFrameTimeText = frame.Timestamp.ToLocalTime().ToString("dd.MM.yyyy · HH:mm:ss.fff");
            AutoWindowLevelCommand.NotifyCanExecuteChanged();
            ResetWindowLevelCommand.NotifyCanExecuteChanged();
        });

        if (queueRender) QueueLatestRender(frame);
    }

    private void QueueCurrentFrameForRender()
    {
        if (Volatile.Read(ref _suppressWindowRender) != 0) return;
        var frame = Volatile.Read(ref _currentRawFrame);
        if (frame is not null) QueueLatestRender(frame);
    }

    private void QueueLatestRender(RawGray16Frame frame)
    {
        var request = new RenderRequest(
            frame,
            (int)Math.Round(WindowCenter),
            Math.Max(1, (int)Math.Round(WindowWidth)),
            Interlocked.Increment(ref _renderVersion));
        Interlocked.Exchange(ref _pendingRender, request);
        StartRenderWorkerIfNeeded();
    }

    private void StartRenderWorkerIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _renderWorkerRunning, 1, 0) != 0) return;
        _ = Task.Run(DrainRenderQueueAsync);
    }

    private async Task DrainRenderQueueAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _pendingRender, null) is { } request)
            {
                try
                {
                    var bmpBytes = _renderer.RenderToBmp(request.Frame, request.Center, request.Width);
                    // Render sırasında daha yeni bir istek geldiyse eski sonucu UI'a gönderme.
                    if (request.Version == Volatile.Read(ref _renderVersion) &&
                        ReferenceEquals(request.Frame, Volatile.Read(ref _currentRawFrame)))
                        SetCurrentImage(bmpBytes);
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _pendingRender) is null)
                        MainThread.BeginInvokeOnMainThread(() => StatusMessage = $"Görüntü render hatası: {ex.Message}");
                }

                await Task.Yield();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _renderWorkerRunning, 0);
            if (Volatile.Read(ref _pendingRender) is not null)
                StartRenderWorkerIfNeeded();
        }
    }

    private void SetCurrentImage(byte[] bmpBytes)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentImage = ImageSource.FromStream(() => new MemoryStream(bmpBytes, writable: false));
            HasCurrentImage = true;
        });
    }
}
