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
using XRaySystem.Domain.ValueObjects;
using XRaySystem.WPF.Services;
using XRaySystem.Domain.WindowLevel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
    private readonly WindowLevelRenderer _windowLevelRenderer;
    private readonly SemaphoreSlim _historyLoadLock = new(1, 1);
    private int _sessionGeneration;
    private PendingMeasurement? _pendingLiveMeasurement;
    private int _liveDispatchScheduled;
    private long _lastFrameUtcTicks;
    private readonly DispatcherTimer _frameFreshnessTimer;

    private sealed record PendingMeasurement(XRayMeasurement Measurement, int Generation);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartGrabCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    private string _deviceStatus = XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartGrabCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyPropertyChangedFor(nameof(CanSelectSnapFrameMode))]
    private bool _isCapturing = false;

    [ObservableProperty]
    private SensorDataDto? _latestTelemetry;

    [ObservableProperty]
    private System.Windows.Media.ImageSource? _currentFrame;

    [ObservableProperty]
    private string _currentFrameResolution = "RES: —";

    [ObservableProperty]
    private string _frameFreshnessText = "NO FRAME";

    [ObservableProperty]
    private string _frameFreshnessColor = "#64748B";

    [ObservableProperty]
    private string _imageStatusBadgeText = "DISCONNECTED";

    [ObservableProperty]
    private string _imageStatusBadgeColor = "#E57373";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(XRayHardwareStateText))]
    [NotifyPropertyChangedFor(nameof(XRayHardwareStateColor))]
    private bool _hasVerifiedHardwareState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(XRayHardwareStateText))]
    [NotifyPropertyChangedFor(nameof(XRayHardwareStateColor))]
    private bool _isXRayActive;

    [ObservableProperty]
    private string _generatorHardwareStateText = "GENERATOR UNVERIFIED";

    [ObservableProperty]
    private string _detectorHardwareStateText = "DETECTOR UNVERIFIED";

    [ObservableProperty]
    private string _acquisitionCoordinatorStateText = "NO STATE";

    [ObservableProperty]
    private string _dataSourceStatusText = "DATA SOURCE: UNVERIFIED";

    [ObservableProperty]
    private string _dataSourceStatusColor = "#64748B";

    public string XRayHardwareStateText => !HasVerifiedHardwareState
        ? "X-RAY STATE UNVERIFIED"
        : IsXRayActive ? "X-RAY ACTIVE" : "X-RAY OFF";

    public string XRayHardwareStateColor => !HasVerifiedHardwareState
        ? "#64748B"
        : IsXRayActive ? "#FF4D5E" : "#32D74B";

    public ObservableCollection<SensorDataDto> TelemetryHistory { get; } = new();
    
    // Snap ile kaydedilen görüntülerin geçmişi
    public ObservableCollection<SensorDataDto> ImageHistory { get; } = new();
    public IReadOnlyList<SnapFrameMode> SnapFrameModeOptions { get; } =
        Enum.GetValues<SnapFrameMode>();

    [ObservableProperty]
    private SensorDataDto? _selectedHistoryItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnapActionText))]
    private SnapFrameMode _selectedSnapFrameMode = SnapFrameMode.Raw;

    public string SnapActionText => "SNAP";

    private RawGray16Frame? _currentRawFrame;
    private string _currentWindowLevelKey = string.Empty;
    private readonly Dictionary<string, (double Center, double Width)> _windowLevelByImage = new();
    private RawDisplayContext _rawDisplayContext;
    private int _liveSessionNumber;
    private string LiveWindowLevelKey => $"live:{_liveSessionNumber}";
    private const string FrozenWindowLevelKey = "frozen-snap";

    private enum RawDisplayContext
    {
        None,
        Live,
        FrozenSnap,
        History
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoWindowLevelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetWindowLevelCommand))]
    private double _wCenter = 32767.5;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoWindowLevelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetWindowLevelCommand))]
    private double _wWidth = 65535;

    public bool HasRawSource => _currentRawFrame is not null;

    public void InvalidateWindowLevelRendering() => _windowLevelRenderer.Invalidate();

    public LiveTelemetryViewModel(
        ISensorService sensorService,
        StartCaptureUseCase startCaptureUseCase,
        ProcessTelemetryUseCase processTelemetryUseCase,
        SaveMeasurementUseCase saveMeasurementUseCase,
        GetMeasurementHistoryUseCase getHistoryUseCase,
        SnapMeasurementUseCase snapMeasurementUseCase,
        ResumeLiveUseCase resumeLiveUseCase,
        IImageStorageService imageStorageService,
        WindowLevelRenderer windowLevelRenderer)
    {
        _sensorService = sensorService;
        _startCaptureUseCase = startCaptureUseCase;
        _processTelemetryUseCase = processTelemetryUseCase;
        _saveMeasurementUseCase = saveMeasurementUseCase;
        _getHistoryUseCase = getHistoryUseCase;
        _snapMeasurementUseCase = snapMeasurementUseCase;
        _resumeLiveUseCase = resumeLiveUseCase;
        _imageStorageService = imageStorageService;
        _windowLevelRenderer = windowLevelRenderer;

        _sensorService.StatusChanged += OnDeviceStatusChanged;
        _sensorService.MeasurementReceived += QueueLatestMeasurement;
        if (_sensorService is IAcquisitionCoordinator coordinator)
            coordinator.HardwareStateChanged += OnHardwareStateChanged;
        _frameFreshnessTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _frameFreshnessTimer.Tick += (_, _) => UpdateFrameFreshness();
        _frameFreshnessTimer.Start();
        ApplyAcquisitionParametersIfValid();
    }

    private void OnHardwareStateChanged(AcquisitionHardwareState state)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            HasVerifiedHardwareState = state.IsVerified;
            IsXRayActive = state.IsVerified && state.XRayActive;
            if (!state.IsVerified)
            {
                GeneratorHardwareStateText = "GENERATOR UNVERIFIED";
                DetectorHardwareStateText = "DETECTOR UNVERIFIED";
                AcquisitionCoordinatorStateText = "STALE / NO CONNECTION";
                return;
            }
            GeneratorHardwareStateText = state.GeneratorConnected ? "GENERATOR CONNECTED" : "GENERATOR OFFLINE";
            DetectorHardwareStateText = state.DetectorConnected
                ? state.DetectorAcquiring ? "DETECTOR ACQUIRING" : "DETECTOR READY"
                : "DETECTOR OFFLINE";
            AcquisitionCoordinatorStateText = state.CoordinatorState.ToString().ToUpperInvariant();
        });
    }

    private void OnDeviceStatusChanged(DeviceStatus status)
    {
        var generation = Volatile.Read(ref _sessionGeneration);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (generation != Volatile.Read(ref _sessionGeneration)) return;
            DeviceStatus = status.ToString();
            IsCapturing = status == XRaySystem.Domain.Entities.DeviceStatus.Grabbing;
            if (status == XRaySystem.Domain.Entities.DeviceStatus.Disconnected)
            {
                ClearLiveRawSource();
                ImageStatusBadgeText = "DISCONNECTED";
                ImageStatusBadgeColor = "#E57373";
            }
            else if (status == XRaySystem.Domain.Entities.DeviceStatus.Connected && !IsPaused && !IsSnapping)
            {
                ImageStatusBadgeText = "CONNECTED · STOPPED";
                ImageStatusBadgeColor = "#D0B36A";
            }
            else if (status == XRaySystem.Domain.Entities.DeviceStatus.Grabbing && !IsPaused)
            {
                ImageStatusBadgeText = "GRAB ACTIVE · 512x512 LOW PREVIEW";
                ImageStatusBadgeColor = "#34C759";
            }
            if (status != XRaySystem.Domain.Entities.DeviceStatus.Grabbing && !IsPaused)
                CurrentFrame = null;
        });
    }

    private void QueueLatestMeasurement(XRayMeasurement measurement)
    {
        Interlocked.Exchange(ref _pendingLiveMeasurement,
            new PendingMeasurement(measurement, Volatile.Read(ref _sessionGeneration)));
        ScheduleLatestMeasurementDispatch();
    }

    private void ScheduleLatestMeasurementDispatch()
    {
        if (Interlocked.CompareExchange(ref _liveDispatchScheduled, 1, 0) != 0) return;

        // UI meşgulse ara kareleri biriktirmek yerine yalnızca en güncel kareyi işler.
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            DrainLatestMeasurement);
    }

    private void DrainLatestMeasurement()
    {
        var pending = Interlocked.Exchange(ref _pendingLiveMeasurement, null);
        Interlocked.Exchange(ref _liveDispatchScheduled, 0);

        if (pending is not null)
            ApplyMeasurement(pending.Measurement, pending.Generation, displayWhilePaused: false);

        if (Volatile.Read(ref _pendingLiveMeasurement) is not null)
            ScheduleLatestMeasurementDispatch();
    }

    private void DisplayMeasurement(XRayMeasurement measurement, bool displayWhilePaused)
    {
        var generation = Volatile.Read(ref _sessionGeneration);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () => ApplyMeasurement(measurement, generation, displayWhilePaused));
    }

    private void ApplyMeasurement(XRayMeasurement measurement, int generation, bool displayWhilePaused)
    {
        if (generation != Volatile.Read(ref _sessionGeneration)) return;
        if (IsPaused && !displayWhilePaused) return;

        var frame = measurement.Frame;
        if (frame is { Pixels.Length: > 0 })
        {
            Interlocked.Exchange(ref _lastFrameUtcTicks, frame.Timestamp.UtcDateTime.Ticks);
            UpdateFrameFreshness();
            SetRawSource(frame, frame.Kind == FrameKind.GrabPreview
                ? LiveWindowLevelKey
                : FrozenWindowLevelKey);
            try { CurrentFrame = CreateRawFallback(frame); } catch { /* malformed source remains unavailable */ }
            try
            {
                _windowLevelRenderer.Submit(frame, WCenter, WWidth, bitmap =>
                {
                    if (generation == Volatile.Read(ref _sessionGeneration) && (!IsPaused || displayWhilePaused))
                        CurrentFrame = bitmap;
                });
            }
            catch
            {
                CurrentFrame = CreateRawFallback(frame);
            }
        }

        var data = _processTelemetryUseCase.Execute(measurement);
        SetDataSourceStatus(measurement.IsSimulator, measurement.SourceMarker);
        LatestTelemetry = data;

        TelemetryHistory.Add(data);
        if (TelemetryHistory.Count > 50)
            TelemetryHistory.RemoveAt(0);

        OnPropertyChanged(nameof(MaxExposureTime));
        OnPropertyChanged(nameof(ExposureTimeIsValid));
        OnPropertyChanged(nameof(ExposureValidationMessage));
    }

    private void UpdateFrameFreshness()
    {
        var ticks = Interlocked.Read(ref _lastFrameUtcTicks);
        var capturedAt = ticks == 0
            ? (DateTimeOffset?)null
            : new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        var result = FrameFreshnessEvaluator.Evaluate(
            capturedAt,
            DateTimeOffset.UtcNow,
            staleAfter: TimeSpan.FromSeconds(5),
            maximumFutureClockSkew: TimeSpan.FromSeconds(2));
        var ageSeconds = Math.Max(0, (int)Math.Floor(result.Age.TotalSeconds));
        (FrameFreshnessText, FrameFreshnessColor) = result.State switch
        {
            FrameFreshnessState.Fresh => ($"LATEST FRAME · {ageSeconds} s", "#32D74B"),
            FrameFreshnessState.Stale => ($"STALE · {ageSeconds} s", "#FFB020"),
            FrameFreshnessState.FutureClockSkew => ("FRAME CLOCK MISMATCH", "#FF4D5E"),
            _ => ("NO FRAME", "#64748B")
        };
    }

    private void SetDataSourceStatus(bool isSimulator, string? sourceMarker)
    {
        switch (DataSourceTrustClassifier.Classify(isSimulator, sourceMarker))
        {
            case DataSourceTrust.Simulation:
                DataSourceStatusText = "DATA SOURCE: SIMULATION / TEST · NOT CLINICAL";
                DataSourceStatusColor = "#D0B36A";
                break;
            case DataSourceTrust.Device:
                DataSourceStatusText = "DATA SOURCE: PHYSICAL DEVICE";
                DataSourceStatusColor = "#32D74B";
                break;
            default:
                DataSourceStatusText = "DATA SOURCE: UNVERIFIED";
                DataSourceStatusColor = "#64748B";
                break;
        }
    }

    private void SetRawSource(RawGray16Frame frame, string key)
    {
        _currentRawFrame = frame;
        _currentWindowLevelKey = key;
        _rawDisplayContext = frame.Kind == FrameKind.GrabPreview
            ? RawDisplayContext.Live
            : RawDisplayContext.FrozenSnap;
        if (_windowLevelByImage.TryGetValue(key, out var state))
        {
            WCenter = state.Center;
            WWidth = state.Width;
        }
        else
        {
            WCenter = 32767.5;
            WWidth = 65535;
        }
        CurrentFrameResolution = $"RES: {frame.Width}x{frame.Height}";
        OnPropertyChanged(nameof(HasRawSource));
        AutoWindowLevelCommand.NotifyCanExecuteChanged();
        ResetWindowLevelCommand.NotifyCanExecuteChanged();
    }

    private void ClearRawSource()
    {
        _currentRawFrame = null;
        _currentWindowLevelKey = string.Empty;
        _rawDisplayContext = RawDisplayContext.None;
        CurrentFrameResolution = "RES: —";
        _windowLevelRenderer.Invalidate();
        OnPropertyChanged(nameof(HasRawSource));
        AutoWindowLevelCommand.NotifyCanExecuteChanged();
        ResetWindowLevelCommand.NotifyCanExecuteChanged();
    }

    private void ClearLiveRawSource()
    {
        if (_rawDisplayContext is RawDisplayContext.Live or RawDisplayContext.None)
            ClearRawSource();
    }

    private static BitmapSource CreateRawFallback(RawGray16Frame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Gray16,
            null, frame.Pixels, frame.StrideBytes);
        bitmap.Freeze();
        return bitmap;
    }

    partial void OnWCenterChanged(double value)
    {
        if (_currentRawFrame is null) return;
        _windowLevelByImage[_currentWindowLevelKey] = (value, WWidth);
        QueueWindowLevelRender();
    }

    partial void OnWWidthChanged(double value)
    {
        if (_currentRawFrame is null) return;
        _windowLevelByImage[_currentWindowLevelKey] = (WCenter, value);
        QueueWindowLevelRender();
    }

    private void QueueWindowLevelRender()
    {
        if (_currentRawFrame is null) return;
        var generation = Volatile.Read(ref _sessionGeneration);
        try { _windowLevelRenderer.Submit(_currentRawFrame, WCenter, WWidth, bitmap => { if (generation == Volatile.Read(ref _sessionGeneration)) CurrentFrame = bitmap; }); }
        catch { /* raw fallback remains displayed */ }
    }

    private bool CanWindowLevel() => _currentRawFrame is not null;

    [RelayCommand(CanExecute = nameof(CanWindowLevel))]
    private async Task AutoWindowLevelAsync()
    {
        var frame = _currentRawFrame;
        if (frame is null) return;
        var generation = Volatile.Read(ref _sessionGeneration);
        var auto = await Task.Run(() => WindowLevelMapper.Auto(frame.Pixels));
        if (!ReferenceEquals(frame, _currentRawFrame) || generation != Volatile.Read(ref _sessionGeneration)) return;
        WCenter = auto.Center;
        WWidth = auto.Width;
    }

    [RelayCommand(CanExecute = nameof(CanWindowLevel))]
    private void ResetWindowLevel()
    {
        WCenter = 32767.5;
        WWidth = 65535;
    }

    private bool CanConnect() =>
        !IsConnecting &&
        DeviceStatus == XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();
    private bool CanStartGrab() =>
        !IsConnecting && !IsCapturing && !IsSnapping &&
        DeviceStatus is nameof(XRaySystem.Domain.Entities.DeviceStatus.Connected) or
            nameof(XRaySystem.Domain.Entities.DeviceStatus.Frozen);
    private bool CanStop() => IsCapturing;
    private bool CanAbort() => IsCapturing || IsSnapping;
    private bool CanDisconnect() => DeviceStatus != XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();
    private bool CanSnap() => DeviceStatus != XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString() && ExposureTimeIsValid && !IsSnapping;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartGrabCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyPropertyChangedFor(nameof(CanSelectSnapFrameMode))]
    private bool _isSnapping = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartGrabCommand))]
    private bool _isConnecting = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartGrabCommand))]
    [NotifyPropertyChangedFor(nameof(StartGrabActionText))]
    private bool _isPaused = false;

    public string StartGrabActionText => "GRAB";
    public bool CanSelectSnapFrameMode => !IsCapturing && !IsSnapping;

    /// <summary>
    /// Kullanıcının girdiği tüp voltajı (kV).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyPropertyChangedFor(nameof(MaxExposureTime))]
    [NotifyPropertyChangedFor(nameof(ExposureTimeIsValid))]
    [NotifyPropertyChangedFor(nameof(ExposureValidationMessage))]
    private double _userVoltage = 70.0;

    /// <summary>
    /// Kullanıcının girdiği tüp gücü (Watt).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyPropertyChangedFor(nameof(MaxExposureTime))]
    [NotifyPropertyChangedFor(nameof(ExposureTimeIsValid))]
    [NotifyPropertyChangedFor(nameof(ExposureValidationMessage))]
    private double _userPowerW = 210.0;

    /// <summary>
    /// Kullanıcının girdiği exposure süresi (ms).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyPropertyChangedFor(nameof(ExposureTimeIsValid))]
    [NotifyPropertyChangedFor(nameof(ExposureValidationMessage))]
    private double _userExposureTime = 200.0;

    partial void OnUserVoltageChanged(double value)
    {
        ApplyAcquisitionParametersIfValid();
    }

    partial void OnUserPowerWChanged(double value)
    {
        ApplyAcquisitionParametersIfValid();
    }

    partial void OnUserExposureTimeChanged(double value) => ApplyAcquisitionParametersIfValid();

    [ObservableProperty]
    private AcquisitionProfile _profile = AcquisitionProfile.IndustrialQaPhantom;
    public Array ProfileOptions => Enum.GetValues<AcquisitionProfile>();

    partial void OnProfileChanged(AcquisitionProfile value)
    {
        _sensorService.Profile = value;
    }

    /// <summary>Min exposure süresi: her zaman 1ms (dedektör sınırı)</summary>
    public double MinExposureTime => 1.0;

    /// <summary>
    /// Max exposure süresi: Kullanıcı girdiği kV ve Watt'a bağlı hesaplanır.
    /// Formül: MaxmAs = 150 - (kV - 40) * 0.8  (kV arttıkça mAs payı azalır)
    ///          MaxExposure = MaxmAs / mA * 1000
    ///          [1ms, 3000ms] arasında sınırlandırılır.
    /// </summary>
    public double MaxExposureTime
    {
        get
        {
            if (!double.IsFinite(UserVoltage) || UserVoltage <= 0 ||
                !double.IsFinite(UserPowerW) || UserPowerW <= 0)
                return MinExposureTime;

            double kv = UserVoltage;
            double mA = UserPowerW / kv;
            double maxMAs = Math.Max(10, 150.0 - (kv - 40.0) * 0.8);
            double maxMs  = (maxMAs / mA) * 1000.0;
            return Math.Clamp(maxMs, 1.0, 3000.0);
        }
    }

    public bool ExposureTimeIsValid =>
        double.IsFinite(UserVoltage) && UserVoltage > 0 &&
        double.IsFinite(UserPowerW) && UserPowerW > 0 &&
        double.IsFinite(UserExposureTime) &&
        UserExposureTime >= MinExposureTime && UserExposureTime <= MaxExposureTime;
    public Visibility ExposureTimeErrorVisibility => ExposureTimeIsValid ? Visibility.Collapsed : Visibility.Visible;
    public string ExposureValidationMessage   => ExposureTimeIsValid
        ? $"Valid range: {MinExposureTime:F0}–{MaxExposureTime:F0} ms"
        : $"kV and W must be positive; time: {MinExposureTime:F0}–{MaxExposureTime:F0} ms";

    private void ApplyAcquisitionParametersIfValid()
    {
        var parameters = new AcquisitionParameters(UserVoltage, UserPowerW, UserExposureTime);
        if (parameters.IsValid)
            _sensorService.Parameters = parameters;
    }

    [RelayCommand(CanExecute = nameof(CanSnap))]
    private async Task SnapAsync()
    {
        // Never start an exposure unless the persistence layer is ready.
        try
        {
            await App.DatabaseReady;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"The database is not ready, so SNAP was not started:\n{ex.Message}",
                "Snap Unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Interlocked.Increment(ref _sessionGeneration);
        string? savedImagePath = null;
        string? savedRawPath = null;
        _windowLevelRenderer.Invalidate();
        try
        {
            IsSnapping = true;
            _sensorService.Profile = Profile;
            ApplyAcquisitionParametersIfValid();

            // Adım 1: Sensörden görüntü al (exposure time kadar bekler)
            var measurement = await _snapMeasurementUseCase.ExecuteAsync(
                new SnapRequest(UserExposureTime, SelectedSnapFrameMode));

            // Publish the immutable snap immediately; persistence must not affect Frozen UI.
            IsPaused = true;
            DisplayMeasurement(measurement, displayWhilePaused: true);
            ImageStatusBadgeText = $"SNAP FROZEN ({measurement.Frame!.Width}x{measurement.Frame.Height})";
            ImageStatusBadgeColor = "#FF9500";

            // Ortak, locale-bağımsız sözleşme: image_voltage_current_exposuretime_date.
            var fileStem = AcquisitionFileNameFactory.CreateStem(measurement);

            // Adım 3a: BitmapSource'u UI (STA) thread'de oluştur ve dondur
            var snapBitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                measurement.Frame!.Width,
                measurement.Frame.Height,
                96, 96,
                System.Windows.Media.PixelFormats.Gray16,
                null,
                measurement.Frame.Pixels,
                 measurement.Frame.StrideBytes);
            snapBitmap.Freeze(); // Dondurulmuş bitmap her thread'den erişilebilir

            // Adım 3b: TIFF dosyasını background thread'de kaydet (yalnızca dosya I/O)
            var savedFiles = await Task.Run(() =>
                _imageStorageService.SaveSnapshot(snapBitmap, measurement.Frame, fileStem));
            savedImagePath = savedFiles.PreviewPath;
            savedRawPath = savedFiles.RawPath;

            // Adım 4: Dosya yolunu entity'e yaz
            measurement.SetImagePath(savedFiles.PreviewPath);
            measurement.SetRawImagePath(savedFiles.RawPath);

            // Adım 5: Veritabanına kaydet (Repository Use Case kullanarak)
            await App.DatabaseReady;
            await _saveMeasurementUseCase.ExecuteAsync(measurement);

            ImageStatusBadgeText = $"SNAP FROZEN ({measurement.Frame.Width}x{measurement.Frame.Height} RAW + TIFF - {savedFiles.FileStem})";

            var dto = _processTelemetryUseCase.Execute(measurement);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                ImageHistory.Insert(0, dto)); // En yeni üstte
        }
        catch (Exception ex)
        {
            // The captured frame remains visible even when TIFF/DB readiness or persistence fails.
            var preservedFileMessage = savedImagePath is not null && savedRawPath is not null &&
                                       File.Exists(savedImagePath) && File.Exists(savedRawPath)
                ? $"\n\nRAW and TIFF files were preserved, but the metadata record may be incomplete:\n{savedRawPath}"
                : string.Empty;
            System.Windows.MessageBox.Show($"{ex.Message}{preservedFileMessage}", "Snap Error");
        }
        finally
        {
            IsSnapping = false;
        }
    }

    [RelayCommand]
    private void LoadFromHistory(SensorDataDto? item)
    {
        Interlocked.Increment(ref _sessionGeneration);
        if (item?.ImagePath == null || !File.Exists(item.ImagePath))
        {
            System.Windows.MessageBox.Show("Image file not found.", "Error");
            return;
        }

        try
        {
            // Keep the command path on the same validated raw-source route as
            // the history list's code-behind handler.
            LoadHistoryTiff(item.ImagePath, item);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Unable to load image: {ex.Message}", "Error");
        }
    }

    public void LoadHistoryTiff(string path, SensorDataDto item)
    {
        Interlocked.Increment(ref _sessionGeneration);
        _windowLevelRenderer.Invalidate();
        var bitmap = _imageStorageService.LoadTiff(path);
        if (bitmap.Format != System.Windows.Media.PixelFormats.Gray16 || bitmap.Format.BitsPerPixel != 16)
            throw new InvalidDataException("The history TIFF is not single-channel 16-bit Gray16.");
        var stride = bitmap.PixelWidth * 2;
        var bytes = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(bytes, stride, 0);
        var pixels = new ushort[bitmap.PixelWidth * bitmap.PixelHeight];
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        var tubeCurrentMa = item.Voltage > 0 ? item.PowerW / item.Voltage : 0;
        var settings = new AcquisitionSettings(tubeCurrentMa, item.Voltage, item.ExposureTime, item.Profile);
        var frame = new RawGray16Frame(pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride,
            Gray16PixelFormat.Gray16LittleEndian, FrameKind.Snap, item.Timestamp, item.Id, settings, "history-tiff");
        SetDataSourceStatus(item.IsSimulator, item.SourceMarker);
        SetRawSource(frame, $"history:{(item.Id != Guid.Empty ? item.Id.ToString("N") : Path.GetFullPath(path))}");
        _rawDisplayContext = RawDisplayContext.History;
        IsPaused = true;
        var generation = Volatile.Read(ref _sessionGeneration);
        CurrentFrame = CreateRawFallback(frame);
        _windowLevelRenderer.Submit(frame, WCenter, WWidth, mapped =>
        {
            if (generation == Volatile.Read(ref _sessionGeneration) && IsPaused) CurrentFrame = mapped;
        });
    }

    [RelayCommand(CanExecute = nameof(CanStartGrab))]
    private async Task StartGrabAsync()
    {
        Interlocked.Increment(ref _sessionGeneration);
        _windowLevelRenderer.Invalidate();
        ClearRawSource();
        _liveSessionNumber++;
        _sensorService.Profile = Profile;
        ApplyAcquisitionParametersIfValid();
        if (IsPaused)
            await _resumeLiveUseCase.ExecuteAsync();
        else
            await _startCaptureUseCase.ExecuteAsync();
        IsPaused = false;
        ImageStatusBadgeText  = "GRAB ACTIVE · 512x512 LOW PREVIEW";
        ImageStatusBadgeColor = "#34C759";
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        try
        {
            IsConnecting = true;
            ClearRawSource();
            await _sensorService.ConnectAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Sensor Connection Error");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        Interlocked.Increment(ref _sessionGeneration);
        _windowLevelRenderer.Invalidate();
        await _sensorService.StopCaptureAsync();
        ClearLiveRawSource();
    }

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortAsync()
    {
        Interlocked.Increment(ref _sessionGeneration);
        _windowLevelRenderer.Invalidate();
        await _sensorService.AbortAsync();
        ClearLiveRawSource();
        IsPaused = false;
    }
    
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        Interlocked.Increment(ref _sessionGeneration);
        _windowLevelRenderer.Invalidate();
        await _sensorService.DisconnectAsync();
        ClearLiveRawSource();
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        var generation = Interlocked.Increment(ref _sessionGeneration);
        await _historyLoadLock.WaitAsync();
        try
        {
            await App.DatabaseReady;
            var history = await _getHistoryUseCase.ExecuteAsync(maximumCount: 200);
            if (generation != Volatile.Read(ref _sessionGeneration)) return;
            
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
            System.Diagnostics.Debug.WriteLine($"RefreshHistoryAsync error: {ex.Message}");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                System.Windows.MessageBox.Show(
                    $"Unable to load history records:\n{ex.Message}",
                    "History Load Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning));
        }
        finally
        {
            _historyLoadLock.Release();
        }
    }
}
