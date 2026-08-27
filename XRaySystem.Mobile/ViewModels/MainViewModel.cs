using System.IO;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using XRaySystem.Application.UseCases;
using XRaySystem.Application.DTOs;
using XRaySystem.Contracts;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Domain.WindowLevel;
using XRaySystem.Infrastructure.Concurrency;
using XRaySystem.Infrastructure.Persistence;
using XRaySystem.Mobile.Services;
using XRaySystem.RemoteClient;

namespace XRaySystem.Mobile.ViewModels;

public enum MobileDialogSeverity
{
    Warning,
    Error
}

public sealed record MobileDialogRequest(
    string Title,
    string Message,
    MobileDialogSeverity Severity);

public partial class MainViewModel : ObservableObject
{
    private const double DefaultVoltageKv = 70.0;
    private const double DefaultPowerW = 210.0;
    private const double DefaultExposureTimeMs = 200.0;

    private readonly ISensorService _sensorService;
    private readonly SnapMeasurementUseCase _snapMeasurement;
    private readonly SaveMeasurementUseCase _saveMeasurement;
    private readonly GetMeasurementHistoryUseCase _getHistory;
    private readonly BoundedMeasurementFileCache _historyCache;
    private readonly MauiImageStorageService _imageStorage;
    private readonly MauiWindowLevelRenderer _renderer;
    private readonly MobileSensorModeSettings _sensorModeSettings;
    private readonly DatabaseInitializationState _databaseInitialization;
    private readonly SensorRuntimeOptions _activeRuntime;
    private readonly IRemoteDeviceClient? _remoteDeviceClient;
    private readonly SemaphoreSlim _historyRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _remoteHistorySyncLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RemoteImageMetadataContract> _remoteHistoryMetadata = new();
    private readonly ConcurrentQueue<Guid> _pendingRemoteHistoryImageIds = new();

    private CancellationTokenSource? _snapCts;
    private RawGray16Frame? _currentRawFrame;
    private readonly LatestValueWorker<RenderRequest> _renderWorker;
    private int _suppressWindowRender;
    private long _renderVersion;
    private long _lastFrameUtcTicks;
    private IDispatcherTimer? _frameFreshnessTimer;
    private bool _historyInitialized;
    private int _remoteHistoryRefreshScheduled;

    public event Action? ReviewImageOpened;
    public event Action<byte[], int, int>? PresentationFrameReady;
    public event Action<MobileDialogRequest>? DialogRequested;

    private sealed record RenderRequest(RawGray16Frame Frame, int Center, int Width, long Version);

    public IReadOnlyList<string> ConnectionModeOptions { get; } =
        ["Local mock", "Remote DeviceHost"];
    public IReadOnlyList<SnapFrameMode> SnapFrameModeOptions { get; } =
        Enum.GetValues<SnapFrameMode>();

    public ObservableCollection<SensorDataDto> RecentImages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectSnapFrameMode))]
    private DeviceStatus _status = DeviceStatus.Disconnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartLiveCommand))]
    [NotifyPropertyChangedFor(nameof(AcquisitionParametersAreValid))]
    [NotifyPropertyChangedFor(nameof(ParametersValidationMessage))]
    [NotifyPropertyChangedFor(nameof(ParametersAccentColor))]
    private double _kv = DefaultVoltageKv;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartLiveCommand))]
    [NotifyPropertyChangedFor(nameof(AcquisitionParametersAreValid))]
    [NotifyPropertyChangedFor(nameof(ParametersValidationMessage))]
    [NotifyPropertyChangedFor(nameof(ParametersAccentColor))]
    private double _watt = DefaultPowerW;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartLiveCommand))]
    [NotifyPropertyChangedFor(nameof(AcquisitionParametersAreValid))]
    [NotifyPropertyChangedFor(nameof(ParametersValidationMessage))]
    [NotifyPropertyChangedFor(nameof(ParametersAccentColor))]
    private double _exposureTime = DefaultExposureTimeMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteConnectionMode))]
    private int _selectedConnectionModeIndex;

    [ObservableProperty]
    private string _deviceHostUrl = string.Empty;

    [ObservableProperty]
    private string _remoteDeviceId = "mock-01";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnapActionText))]
    private SnapFrameMode _selectedSnapFrameMode = SnapFrameMode.Raw;

    [ObservableProperty]
    private string _remoteApiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceHostCredentialStatusText))]
    private bool _hasStoredDeviceHostApiKey;

    [ObservableProperty]
    private ImageSource? _currentImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCurrentImage))]
    private bool _hasCurrentImage;

    [ObservableProperty]
    private string _statusMessage = "System ready. Waiting for a device connection.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(XRayHardwareStateText))]
    [NotifyPropertyChangedFor(nameof(XRayHardwareStateColor))]
    private bool _hasVerifiedHardwareState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(XRayHardwareStateText))]
    [NotifyPropertyChangedFor(nameof(XRayHardwareStateColor))]
    private bool _isXRayActive;

    [ObservableProperty]
    private string _generatorHardwareStateText = "UNVERIFIED";

    [ObservableProperty]
    private string _detectorHardwareStateText = "UNVERIFIED";

    [ObservableProperty]
    private string _acquisitionCoordinatorStateText = "NO STATE";

    [ObservableProperty]
    private string _dataSourceStatusText = "DATA SOURCE: UNVERIFIED";

    [ObservableProperty]
    private string _dataSourceStatusColor = "#667991";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectSnapFrameMode))]
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
    private string _lastFrameTimeText = "No image acquired yet";

    [ObservableProperty]
    private string _frameFreshnessText = "NO FRAME";

    [ObservableProperty]
    private string _frameFreshnessColor = "#667991";

    [ObservableProperty]
    private int _currentFrameWidth;

    [ObservableProperty]
    private int _currentFrameHeight;

    [ObservableProperty]
    private double _windowCenter = 32767.5;

    [ObservableProperty]
    private double _windowWidth = 65535;

    public bool HasNoCurrentImage => !HasCurrentImage;
    public bool HasNoRecentImages => !HasRecentImages;
    public bool IsRemoteConnectionMode => SelectedConnectionModeIndex == 1;
    public string ActiveConnectionModeText => _activeRuntime.Mode == SensorRuntimeMode.Remote
        ? "ACTIVE: REMOTE"
        : "ACTIVE: LOCAL MOCK";
    public string DeviceHostCredentialStatusText => HasStoredDeviceHostApiKey
        ? "API key stored in secure storage."
        : "No API key is stored.";
    public string SnapActionText => "SNAP";
    public bool CanSelectSnapFrameMode => !IsBusy && Status != DeviceStatus.Grabbing;

    public bool AcquisitionParametersAreValid =>
        new AcquisitionParameters(Kv, Watt, ExposureTime).IsValid;

    public string ParametersValidationMessage => AcquisitionParametersAreValid
        ? $"Ready · {Kv:F0} kV · {Watt:F0} W · {ExposureTime:F0} ms"
        : "kV and W must be above zero; exposure must be between 1 and 3000 ms.";

    public string ParametersAccentColor => AcquisitionParametersAreValid ? "#32D74B" : "#FF4D5E";

    public string StatusDisplayText => Status switch
    {
        DeviceStatus.Connected => "READY",
        DeviceStatus.Grabbing => "GRAB",
        DeviceStatus.Snapping => "SNAP",
        DeviceStatus.Frozen => "FROZEN",
        DeviceStatus.Error => "ERROR",
        _ => "DISCONNECTED"
    };

    public string StartLiveActionText => "GRAB";

    public string StatusAccentColor => Status switch
    {
        DeviceStatus.Connected => "#32D74B",
        DeviceStatus.Grabbing => "#00E5FF",
        DeviceStatus.Snapping => "#FFB020",
        DeviceStatus.Frozen => "#FFB020",
        DeviceStatus.Error => "#FF4D5E",
        _ => "#667991"
    };

    public string StatusBadgeBackground => Status switch
    {
        DeviceStatus.Connected => "#163A27",
        DeviceStatus.Grabbing => "#123844",
        DeviceStatus.Snapping => "#3C2A13",
        DeviceStatus.Frozen => "#3C2A13",
        DeviceStatus.Error => "#351720",
        _ => "#182131"
    };

    public string XRayHardwareStateText => !HasVerifiedHardwareState
        ? "X-RAY STATE UNVERIFIED"
        : IsXRayActive
            ? "X-RAY ACTIVE"
            : "X-RAY OFF";

    public string XRayHardwareStateColor => !HasVerifiedHardwareState
        ? "#667991"
        : IsXRayActive
            ? "#FF4D5E"
            : "#32D74B";

    public MainViewModel(
        ISensorService sensorService,
        SnapMeasurementUseCase snapMeasurement,
        SaveMeasurementUseCase saveMeasurement,
        GetMeasurementHistoryUseCase getHistory,
        BoundedMeasurementFileCache historyCache,
        MauiImageStorageService imageStorage,
        MauiWindowLevelRenderer renderer,
        MobileSensorModeSettings sensorModeSettings,
        DatabaseInitializationState databaseInitialization,
        SensorRuntimeOptions activeRuntime,
        IRemoteDeviceClient? remoteDeviceClient = null)
    {
        _sensorService = sensorService;
        _snapMeasurement = snapMeasurement;
        _saveMeasurement = saveMeasurement;
        _getHistory = getHistory;
        _historyCache = historyCache;
        _imageStorage = imageStorage;
        _renderer = renderer;
        _sensorModeSettings = sensorModeSettings;
        _databaseInitialization = databaseInitialization;
        _activeRuntime = activeRuntime;
        _remoteDeviceClient = remoteDeviceClient;
        _renderWorker = new LatestValueWorker<RenderRequest>(
            ProcessRenderRequest,
            ReportRenderFailure);
        _sensorService.Profile = AcquisitionProfile.IndustrialQaPhantom;
        SelectedConnectionModeIndex = activeRuntime.Mode == SensorRuntimeMode.Remote ? 1 : 0;
        DeviceHostUrl = activeRuntime.DeviceHostBaseAddress.AbsoluteUri;
        RemoteDeviceId = activeRuntime.DeviceId;

        _sensorService.StatusChanged += status =>
            MainThread.BeginInvokeOnMainThread(() => Status = status);

        _sensorService.MeasurementReceived += measurement => AcceptMeasurement(measurement, queueRender: true);
        if (_sensorService is IAcquisitionCoordinator coordinator)
            coordinator.HardwareStateChanged += OnHardwareStateChanged;
        if (_remoteDeviceClient is not null)
            _remoteDeviceClient.FrameReadyReceived += OnRemoteFrameReady;
    }

    private bool CanConnect() => !IsBusy && Status == DeviceStatus.Disconnected;
    private bool CanDisconnect() => !IsBusy && Status != DeviceStatus.Disconnected;
    private bool CanStartLive() =>
        !IsBusy &&
        (Status is DeviceStatus.Connected or DeviceStatus.Frozen) &&
        AcquisitionParametersAreValid;
    private bool CanStopLive() => Status == DeviceStatus.Grabbing;
    private bool CanAbort() => Status is DeviceStatus.Grabbing or DeviceStatus.Snapping;
    private bool CanSnap() =>
        !IsBusy &&
        (Status is DeviceStatus.Connected or DeviceStatus.Grabbing or DeviceStatus.Frozen) &&
        AcquisitionParametersAreValid;
    private bool CanAdjustImage() => Volatile.Read(ref _currentRawFrame) is not null;
    private bool CanApplyConnectionSettings() => !IsBusy && Status == DeviceStatus.Disconnected;

    private void ApplyAcquisitionParameters()
    {
        var parameters = new AcquisitionParameters(Kv, Watt, ExposureTime);
        parameters.EnsureValid();
        _sensorService.Parameters = parameters;
        _sensorService.Profile = AcquisitionProfile.IndustrialQaPhantom;
    }

    partial void OnStatusChanged(DeviceStatus value)
    {
        OnPropertyChanged(nameof(StatusDisplayText));
        OnPropertyChanged(nameof(StatusAccentColor));
        OnPropertyChanged(nameof(StatusBadgeBackground));
        OnPropertyChanged(nameof(StartLiveActionText));
        NotifyCommandStates();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();

    partial void OnSelectedConnectionModeIndexChanged(int value) =>
        ApplyConnectionSettingsCommand.NotifyCanExecuteChanged();

    partial void OnDeviceHostUrlChanged(string value) =>
        ApplyConnectionSettingsCommand.NotifyCanExecuteChanged();

    partial void OnRemoteDeviceIdChanged(string value) =>
        ApplyConnectionSettingsCommand.NotifyCanExecuteChanged();

    partial void OnRemoteApiKeyChanged(string value) =>
        ApplyConnectionSettingsCommand.NotifyCanExecuteChanged();

    partial void OnHasStoredDeviceHostApiKeyChanged(bool value) =>
        ClearDeviceHostApiKeyCommand.NotifyCanExecuteChanged();

    partial void OnWindowCenterChanged(double value) => QueueCurrentFrameForRender();
    partial void OnWindowWidthChanged(double value) => QueueCurrentFrameForRender();

    private void NotifyCommandStates()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        StartLiveCommand.NotifyCanExecuteChanged();
        StopLiveCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
        SnapCommand.NotifyCanExecuteChanged();
        AutoWindowLevelCommand.NotifyCanExecuteChanged();
        ResetWindowLevelCommand.NotifyCanExecuteChanged();
        ApplyConnectionSettingsCommand.NotifyCanExecuteChanged();
        ClearDeviceHostApiKeyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApplyConnectionSettings))]
    private async Task ApplyConnectionSettingsAsync()
    {
        try
        {
            var mode = IsRemoteConnectionMode
                ? SensorRuntimeMode.Remote
                : SensorRuntimeMode.Local;
            var url = new Uri(DeviceHostUrl.Trim(), UriKind.Absolute);
            var options = new SensorRuntimeOptions(mode, url, RemoteDeviceId.Trim());
            var apiKeySaved = false;
            if (mode == SensorRuntimeMode.Remote && !string.IsNullOrWhiteSpace(RemoteApiKey))
            {
                if (RemoteApiKey.Trim().Length < 32)
                    throw new ArgumentException("The DeviceHost API key must contain at least 32 characters.");
                await _sensorModeSettings.SaveApiKeyAsync(RemoteApiKey);
                RemoteApiKey = string.Empty;
                HasStoredDeviceHostApiKey = true;
                apiKeySaved = true;
            }
            _sensorModeSettings.Save(options);
            StatusMessage = mode == _activeRuntime.Mode &&
                            url == _activeRuntime.DeviceHostBaseAddress &&
                            string.Equals(options.DeviceId, _activeRuntime.DeviceId, StringComparison.Ordinal)
                ? apiKeySaved
                    ? "Connection settings are active; the API key was saved to secure storage."
                    : "Connection settings are already active."
                : "Connection settings saved. Restart the app to apply them.";
        }
        catch (Exception ex)
        {
            ReportError(
                "Invalid Connection Settings",
                $"Invalid connection settings: {ex.Message}",
                MobileDialogSeverity.Warning);
        }
    }

    private bool CanClearDeviceHostApiKey() =>
        !IsBusy && Status == DeviceStatus.Disconnected && HasStoredDeviceHostApiKey;

    [RelayCommand(CanExecute = nameof(CanClearDeviceHostApiKey))]
    private void ClearDeviceHostApiKey()
    {
        try
        {
            _sensorModeSettings.ClearApiKey();
            RemoteApiKey = string.Empty;
            HasStoredDeviceHostApiKey = false;
            StatusMessage = "The stored DeviceHost API key was deleted from secure storage.";
        }
        catch (Exception ex)
        {
            ReportError(
                "Credential Error",
                $"The API key could not be deleted: {ex.Message}");
        }
    }

    public async Task InitializeAsync()
    {
        StartFrameFreshnessTimer();
        if (_historyInitialized) return;
        _historyInitialized = true;
        await Task.Yield();
        var databaseInitialization = _databaseInitialization.EnsureInitializedAsync();
        try
        {
            HasStoredDeviceHostApiKey =
                !string.IsNullOrWhiteSpace(await _sensorModeSettings.GetApiKeyAsync());
        }
        catch (Exception ex)
        {
            ReportError(
                "Credential Error",
                $"Secure connection credentials could not be read: {ex.Message}");
        }
        await databaseInitialization;
        if (_databaseInitialization.HasFailed)
        {
            ReportError(
                "Local Storage Unavailable",
                "The local history database could not be initialized; saving and history are unavailable.");
            return;
        }
        try
        {
            await _historyCache.EnforceAsync(keepCount: 30);
            await _historyCache.ReconcileAsync();
        }
        catch (Exception ex)
        {
            // A damaged or temporarily locked cache must not prevent the control
            // surface from opening. The reconciliation is retryable and never
            // affects the authoritative DeviceHost archive.
            System.Diagnostics.Debug.WriteLine($"Mobile cache reconciliation error: {ex.Message}");
        }
        await RefreshHistoryCoreAsync(synchronizeRemote: false);
        if (_remoteDeviceClient is not null)
            _ = RefreshRemoteHistoryAfterStartupAsync();
    }

    public async Task HandleWindowStoppedAsync()
    {
        _snapCts?.Cancel();
        if (Status is not (DeviceStatus.Grabbing or DeviceStatus.Snapping))
            return;

        try
        {
            if (Status == DeviceStatus.Snapping)
                await _sensorService.AbortAsync();
            else
                await _sensorService.StopCaptureAsync();
            StatusMessage = "The app entered the background; acquisition was stopped safely.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Acquisition could not be stopped during backgrounding: {ex.Message}";
        }
    }

    public async Task HandleWindowDestroyingAsync()
    {
        _snapCts?.Cancel();
        _renderWorker.Dispose();
        _frameFreshnessTimer?.Stop();
        if (_sensorService is IAcquisitionCoordinator coordinator)
            coordinator.HardwareStateChanged -= OnHardwareStateChanged;
        if (_remoteDeviceClient is not null)
            _remoteDeviceClient.FrameReadyReceived -= OnRemoteFrameReady;
        if (Status == DeviceStatus.Disconnected)
            return;

        try
        {
            await _sensorService.DisconnectAsync();
        }
        catch
        {
            // Uygulama kapanışı sürerken UI'ya güvenilir biçimde hata aktarılamaz.
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        StatusMessage = "Connecting to the sensor...";
        try
        {
            await _sensorService.ConnectAsync();
            StatusMessage = "Sensor connected. Parameters are ready to apply.";
        }
        catch (Exception ex)
        {
            ReportError("Connection Failed", $"Connection failed: {ex.Message}");
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
        StatusMessage = "Disconnecting the sensor safely...";
        _snapCts?.Cancel();
        try
        {
            await _sensorService.DisconnectAsync();
            StatusMessage = "Sensor disconnected. The latest image remains on screen.";
        }
        catch (Exception ex)
        {
            ReportError("Disconnect Failed", $"Disconnect failed: {ex.Message}");
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
        StatusMessage = "Preparing the low-resolution Gray16 GRAB stream...";
        try
        {
            ApplyAcquisitionParameters();
            await _sensorService.StartCaptureAsync();
            StatusMessage = "GRAB active · latest-frame mode enabled.";
        }
        catch (Exception ex)
        {
            ReportError("GRAB Failed", $"GRAB could not start: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopLive))]
    private async Task StopLiveAsync()
    {
        StatusMessage = "Freezing GRAB...";
        try
        {
            await _sensorService.StopCaptureAsync();
            StatusMessage = "GRAB frozen. The latest verified image is preserved.";
        }
        catch (Exception ex)
        {
            ReportError("Freeze Failed", $"GRAB could not be frozen: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortAsync()
    {
        StatusMessage = "Routing the active acquisition to safe shutdown...";
        _snapCts?.Cancel();
        try
        {
            await _sensorService.AbortAsync();
            StatusMessage = "Acquisition aborted. Device state was verified again.";
        }
        catch (Exception ex)
        {
            ReportError("Stop Failed", $"Abort failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanSnap))]
    private async Task SnapAsync()
    {
        IsBusy = true;
        StatusMessage = "Preparing a high-resolution SNAP...";
        _snapCts?.Dispose();
        _snapCts = new CancellationTokenSource();
        MobileSnapshotFiles? savedFiles = null;

        try
        {
            await _databaseInitialization.EnsureInitializedAsync();
            if (!_databaseInitialization.IsReady)
                throw new InvalidOperationException("Local storage is unavailable; SNAP was not started.");
            ApplyAcquisitionParameters();
            var measurement = await _snapMeasurement.ExecuteAsync(
                new SnapRequest(ExposureTime, SelectedSnapFrameMode),
                _snapCts.Token);
            if (measurement.Frame is null) return;

            AcceptMeasurement(measurement, queueRender: false);
            var center = (int)Math.Round(WindowCenter);
            var width = Math.Max(1, (int)Math.Round(WindowWidth));
            var bmpBytes = await Task.Run(() =>
                _renderer.RenderToBmp(measurement.Frame, center, width), _snapCts.Token);
            SetCurrentImage(bmpBytes, measurement.Frame.Width, measurement.Frame.Height);

            var fileStem = AcquisitionFileNameFactory.CreateStem(measurement);
            savedFiles = await Task.Run(
                () => _imageStorage.SaveSnapshot(bmpBytes, measurement.Frame, fileStem),
                _snapCts.Token);

            measurement.SetImagePath(savedFiles.PreviewPath);
            measurement.SetRawImagePath(savedFiles.RawPath);
            await _saveMeasurement.ExecuteAsync(measurement);
            await RefreshHistoryAsync();
            StatusMessage = $"SNAP saved and frozen for review · 16-bit TIFF + preview · {Path.GetFileNameWithoutExtension(savedFiles.RawPath)}. Select GRAB to continue.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "The acquisition was canceled by the user or app lifecycle.";
        }
        catch (Exception ex)
        {
            var preserved = savedFiles is null
                ? string.Empty
                : " · RAW and preview files were preserved; the metadata record may be incomplete.";
            ReportError("SNAP Failed", $"SNAP failed: {ex.Message}{preserved}");
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
        Kv = DefaultVoltageKv;
        Watt = DefaultPowerW;
        ExposureTime = DefaultExposureTimeMs;
        StatusMessage = "Acquisition parameters were reset to defaults.";
    }

    [RelayCommand]
    private Task RefreshHistoryAsync() => RefreshHistoryCoreAsync(synchronizeRemote: true);

    private async Task RefreshHistoryCoreAsync(bool synchronizeRemote)
    {
        await _historyRefreshLock.WaitAsync();
        IsHistoryLoading = true;
        try
        {
            await _databaseInitialization.EnsureInitializedAsync();
            if (!_databaseInitialization.IsReady)
                throw new InvalidOperationException("Local history storage is unavailable.");
            if (synchronizeRemote && _remoteDeviceClient is not null)
            {
                try
                {
                    await SynchronizeRemoteHistoryAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Remote history sync error: {ex.Message}");
                }
            }
            await _historyCache.EnforceAsync(keepCount: 30);
            var items = (await _getHistory.ExecuteAsync(maximumCount: 30))
                .Where(item =>
                    (!string.IsNullOrWhiteSpace(item.RawImagePath) && File.Exists(item.RawImagePath)) ||
                    (!string.IsNullOrWhiteSpace(item.ImagePath) &&
                     Path.GetExtension(item.ImagePath).Equals(".bmp", StringComparison.OrdinalIgnoreCase) &&
                     File.Exists(item.ImagePath)))
                .Take(30)
                .ToList();

            RecentImages.Clear();
            foreach (var item in items) RecentImages.Add(item);
            HasRecentImages = RecentImages.Count > 0;
        }
        catch (Exception ex)
        {
            ReportError(
                "History Unavailable",
                $"History could not be loaded: {ex.Message}",
                MobileDialogSeverity.Warning);
        }
        finally
        {
            IsHistoryLoading = false;
            _historyRefreshLock.Release();
        }
    }

    private async Task RefreshRemoteHistoryAfterStartupAsync()
    {
        try
        {
            await Task.Yield();
            await RefreshHistoryCoreAsync(synchronizeRemote: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Startup remote history sync error: {ex.Message}");
        }
    }

    private void OnRemoteFrameReady(FrameReadyEventContract frame)
    {
        if (frame.Operation != AcquisitionOperationContract.Snap || frame.ImageId is null)
            return;
        _pendingRemoteHistoryImageIds.Enqueue(frame.ImageId.Value);
        ScheduleRemoteHistoryRefresh();
    }

    private void ScheduleRemoteHistoryRefresh()
    {
        if (Interlocked.Exchange(ref _remoteHistoryRefreshScheduled, 1) != 0)
            return;
        _ = RefreshAfterRemoteFrameAsync();
    }

    private async Task RefreshAfterRemoteFrameAsync()
    {
        try
        {
            // The initiating client may still be completing its own verified local
            // write. A short debounce lets that transaction win and avoids duplicate files.
            await Task.Delay(300);
            while (true)
            {
                while (_pendingRemoteHistoryImageIds.TryDequeue(out _)) { }
                while (IsBusy) await Task.Delay(100);
                await RefreshHistoryCoreAsync(synchronizeRemote: true);
                if (_pendingRemoteHistoryImageIds.IsEmpty) break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FrameReady history refresh error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _remoteHistoryRefreshScheduled, 0);
            if (!_pendingRemoteHistoryImageIds.IsEmpty)
                ScheduleRemoteHistoryRefresh();
        }
    }

    private async Task SynchronizeRemoteHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_remoteDeviceClient is null) return;
        await _remoteHistorySyncLock.WaitAsync(cancellationToken);
        try
        {
            var remoteItems = (await _remoteDeviceClient
                    .GetImagesAsync(30, cancellationToken)
                    .ConfigureAwait(false))
                .OrderByDescending(item => item.CapturedAtUtc)
                .Take(30)
                .ToArray();
            _remoteHistoryMetadata.Clear();
            foreach (var metadata in remoteItems)
                _remoteHistoryMetadata[metadata.ImageId] = metadata;
            var localIds = (await _getHistory.ExecuteAsync(maximumCount: 30))
                .Select(item => item.Id)
                .ToHashSet();

            foreach (var metadata in remoteItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (localIds.Contains(metadata.ImageId)) continue;
                if (!metadata.LowAvailable)
                    throw new InvalidDataException(
                        $"DeviceHost catalog entry {metadata.ImageId:D} is missing its LOW preview.");
                var lowBytes = await _remoteDeviceClient
                    .GetLowImageAsync(metadata.ImageId, cancellationToken)
                    .ConfigureAwait(false);
                var (lowWidth, lowHeight) = ReadBmpDimensions(lowBytes);
                if (lowWidth <= 0 || lowHeight <= 0 ||
                    lowWidth != metadata.LowWidth || lowHeight != metadata.LowHeight)
                    throw new InvalidDataException(
                        $"DeviceHost LOW preview dimensions do not match catalog entry {metadata.ImageId:D}.");

                var measurement = RemoteImageMeasurementFactory.CreateCatalogEntry(metadata);
                var fileStem = AcquisitionFileNameFactory.CreateStem(measurement);
                var previewPath = await Task.Run(
                    () => _imageStorage.SaveRemotePreview(
                        lowBytes,
                        fileStem,
                        measurement.AcquisitionId),
                    cancellationToken);
                measurement.SetImagePath(previewPath);
                await _saveMeasurement.ExecuteIfMissingAsync(measurement);
                localIds.Add(metadata.ImageId);
            }
        }
        finally
        {
            _remoteHistorySyncLock.Release();
        }
    }

    [RelayCommand]
    private async Task LoadHistoryImageAsync(SensorDataDto? item)
    {
        if (item is null || IsBusy) return;
        IsBusy = true;
        try
        {
            if (Status is DeviceStatus.Grabbing or DeviceStatus.Snapping)
            {
                StatusMessage = "Freezing GRAB before opening history...";
                _snapCts?.Cancel();
                if (Status == DeviceStatus.Snapping)
                    await _sensorService.AbortAsync();
                else
                    await _sensorService.StopCaptureAsync();
            }

            StatusMessage = "Verifying and opening the history image...";
            var hasRaw = !string.IsNullOrWhiteSpace(item.RawImagePath) && File.Exists(item.RawImagePath);
            string? rawDownloadWarning = null;
            if (!hasRaw &&
                _remoteDeviceClient is not null &&
                _remoteHistoryMetadata.TryGetValue(item.Id, out var remoteMetadata) &&
                remoteMetadata.RawAvailable &&
                !string.IsNullOrWhiteSpace(item.ImagePath) &&
                File.Exists(item.ImagePath))
            {
                try
                {
                    StatusMessage = "Downloading and verifying the full Gray16 TIFF source...";
                    var rawBytes = await _remoteDeviceClient.GetRawImageAsync(item.Id);
                    var measurement = RemoteImageMeasurementFactory.Create(remoteMetadata, rawBytes);
                    var rawPath = await Task.Run(() =>
                        _imageStorage.SaveRemoteRaw(measurement.Frame!, item.ImagePath!));
                    await _saveMeasurement.UpdateRawImagePathAsync(item.Id, rawPath);
                    item.RawImagePath = rawPath;
                    hasRaw = true;
                }
                catch (Exception ex)
                {
                    rawDownloadWarning = ex.Message;
                    System.Diagnostics.Debug.WriteLine($"On-demand RAW download error: {ex.Message}");
                }
            }
            if (hasRaw)
            {
                var frame = await Task.Run(() => _imageStorage.LoadRaw(item.RawImagePath!));
                Volatile.Write(ref _currentRawFrame, frame);
                Interlocked.Increment(ref _renderVersion);
                QueueLatestRender(frame);
                FrameKindDisplay = "HISTORY · GRAY16 TIFF";
                FrameMetadataText = $"{frame.Width} × {frame.Height} · GRAY16";
                LastFrameTimeText = frame.Timestamp.ToLocalTime().ToString("dd.MM.yyyy · HH:mm:ss");
                SetLastFrameTimestamp(frame.Timestamp);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
                    throw new FileNotFoundException("The image preview could not be found.", item.ImagePath);

                var bytes = await Task.Run(() => _imageStorage.LoadBmp(item.ImagePath));
                Volatile.Write(ref _currentRawFrame, null);
                Interlocked.Increment(ref _renderVersion);
                var (previewWidth, previewHeight) = ReadBmpDimensions(bytes);
                SetCurrentImage(bytes, previewWidth, previewHeight);
                FrameKindDisplay = "HISTORY · PREVIEW";
                FrameMetadataText = "8-BIT BMP · LEGACY";
                LastFrameTimeText = item.Timestamp.ToString("dd.MM.yyyy · HH:mm:ss");
                SetLastFrameTimestamp(new DateTimeOffset(item.Timestamp.ToUniversalTime()));
            }

            MeasuredPowerW = item.PowerW;
            MeasuredVoltageKv = item.Voltage;
            MeasuredExposureMs = item.ExposureTime;
            MeasuredIntensity = item.XRayIntensity;
            SetDataSourceStatus(item.IsSimulator, item.SourceMarker);
            AutoWindowLevelCommand.NotifyCanExecuteChanged();
            ResetWindowLevelCommand.NotifyCanExecuteChanged();
            var openedPath = hasRaw ? item.RawImagePath : item.ImagePath;
            StatusMessage = rawDownloadWarning is null
                ? $"History image opened · {Path.GetFileName(openedPath)}"
                : $"LOW preview opened; full RAW is temporarily unavailable: {rawDownloadWarning}";
            MainThread.BeginInvokeOnMainThread(() => ReviewImageOpened?.Invoke());
        }
        catch (Exception ex)
        {
            ReportError(
                "Image Review Failed",
                $"History image could not be opened: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
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
        StatusMessage = "Automatic Window/Level was calculated from the image histogram.";
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
        StatusMessage = "Window/Level was reset to the full Gray16 range.";
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
            SetDataSourceStatus(measurement.IsSimulator, measurement.SourceMarker);
            FrameKindDisplay = frame.Kind == FrameKind.GrabPreview ? "GRAB PREVIEW" : "SNAP · FROZEN";
            FrameMetadataText = $"{frame.Width} × {frame.Height} · GRAY16";
            LastFrameTimeText = frame.Timestamp.ToLocalTime().ToString("dd.MM.yyyy · HH:mm:ss.fff");
            SetLastFrameTimestamp(frame.Timestamp);
            AutoWindowLevelCommand.NotifyCanExecuteChanged();
            ResetWindowLevelCommand.NotifyCanExecuteChanged();
        });

        if (queueRender) QueueLatestRender(frame);
    }

    private void SetDataSourceStatus(bool isSimulator, string? sourceMarker)
    {
        switch (DataSourceTrustClassifier.Classify(isSimulator, sourceMarker))
        {
            case DataSourceTrust.Simulation:
                DataSourceStatusText = "DATA SOURCE: SIMULATION / TEST · NOT FOR CLINICAL USE";
                DataSourceStatusColor = "#FFB020";
                break;
            case DataSourceTrust.Device:
                DataSourceStatusText = "DATA SOURCE: PHYSICAL DEVICE";
                DataSourceStatusColor = "#32D74B";
                break;
            default:
                DataSourceStatusText = "DATA SOURCE: UNVERIFIED";
                DataSourceStatusColor = "#667991";
                break;
        }
    }

    private void OnHardwareStateChanged(AcquisitionHardwareState state)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            HasVerifiedHardwareState = state.IsVerified;
            IsXRayActive = state.IsVerified && state.XRayActive;
            if (!state.IsVerified)
            {
                GeneratorHardwareStateText = "GENERATOR UNVERIFIED";
                DetectorHardwareStateText = "DETECTOR UNVERIFIED";
                AcquisitionCoordinatorStateText = "STALE / DISCONNECTED";
                return;
            }
            GeneratorHardwareStateText = state.GeneratorConnected ? "GENERATOR CONNECTED" : "GENERATOR OFFLINE";
            DetectorHardwareStateText = state.DetectorConnected
                ? state.DetectorAcquiring ? "DETECTOR ACQUIRING" : "DETECTOR READY"
                : "DETECTOR OFFLINE";
            AcquisitionCoordinatorStateText = state.CoordinatorState.ToString().ToUpperInvariant();
        });
    }

    private void StartFrameFreshnessTimer()
    {
        if (_frameFreshnessTimer is not null ||
            Microsoft.Maui.Controls.Application.Current?.Dispatcher is not { } dispatcher)
            return;
        _frameFreshnessTimer = dispatcher.CreateTimer();
        _frameFreshnessTimer.Interval = TimeSpan.FromSeconds(1);
        _frameFreshnessTimer.Tick += (_, _) => UpdateFrameFreshness();
        _frameFreshnessTimer.Start();
        UpdateFrameFreshness();
    }

    private void SetLastFrameTimestamp(DateTimeOffset timestamp)
    {
        Interlocked.Exchange(ref _lastFrameUtcTicks, timestamp.UtcDateTime.Ticks);
        UpdateFrameFreshness();
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
            _ => ("NO FRAME", "#667991")
        };
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
        _renderWorker.TryPost(request);
    }

    private ValueTask ProcessRenderRequest(RenderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bmpBytes = _renderer.RenderToBmp(request.Frame, request.Center, request.Width);
        cancellationToken.ThrowIfCancellationRequested();
        // Render sırasında daha yeni bir istek geldiyse eski sonucu UI'a gönderme.
        if (request.Version == Volatile.Read(ref _renderVersion) &&
            ReferenceEquals(request.Frame, Volatile.Read(ref _currentRawFrame)))
            SetCurrentImage(bmpBytes, request.Frame.Width, request.Frame.Height);
        return ValueTask.CompletedTask;
    }

    private void ReportRenderFailure(RenderRequest request, Exception exception)
    {
        if (request.Version != Volatile.Read(ref _renderVersion) ||
            !ReferenceEquals(request.Frame, Volatile.Read(ref _currentRawFrame)))
            return;
        MainThread.BeginInvokeOnMainThread(() =>
            StatusMessage = $"Image rendering failed: {exception.Message}");
    }

    private void ReportError(
        string title,
        string message,
        MobileDialogSeverity severity = MobileDialogSeverity.Error)
    {
        void Publish()
        {
            StatusMessage = message;
            DialogRequested?.Invoke(new MobileDialogRequest(title, message, severity));
        }

        if (MainThread.IsMainThread) Publish();
        else MainThread.BeginInvokeOnMainThread(Publish);
    }

    private void SetCurrentImage(byte[] bmpBytes, int width, int height)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
#if !ANDROID
            CurrentImage = ImageSource.FromStream(() => new MemoryStream(bmpBytes, writable: false));
#endif
            CurrentFrameWidth = Math.Max(0, width);
            CurrentFrameHeight = Math.Max(0, height);
            HasCurrentImage = true;
            PresentationFrameReady?.Invoke(bmpBytes, CurrentFrameWidth, CurrentFrameHeight);
        });
    }

    private static (int Width, int Height) ReadBmpDimensions(byte[] bmpBytes)
    {
        if (bmpBytes.Length < 26 || bmpBytes[0] != (byte)'B' || bmpBytes[1] != (byte)'M')
            return (0, 0);

        var width = BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.AsSpan(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.AsSpan(22, 4));
        return (Math.Max(0, width), Math.Abs(signedHeight));
    }
}
