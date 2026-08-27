using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
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
using XRaySystem.WPF.Views;
using XRaySystem.Domain.WindowLevel;
using XRaySystem.Contracts;
using XRaySystem.Infrastructure.Storage;
using XRaySystem.RemoteClient;
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
    private readonly IRemoteDeviceClient? _remoteDeviceClient;
    private readonly SemaphoreSlim _historyLoadLock = new(1, 1);
    private int _remoteHistoryRefreshScheduled;
    private readonly ConcurrentQueue<Guid> _pendingRemoteHistoryImageIds = new();
    private long _lastAutoDisplayedRemoteSnapUtcTicks;
    private int _sessionGeneration;
    private PendingMeasurement? _pendingLiveMeasurement;
    private int _liveDispatchScheduled;
    private long _lastFrameUtcTicks;
    private readonly DispatcherTimer _frameFreshnessTimer;

    private sealed record PendingMeasurement(
        XRayMeasurement Measurement,
        int Generation,
        bool ForceLiveDisplay);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartGrabCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyPropertyChangedFor(nameof(IsDeviceObservedConnected))]
    [NotifyPropertyChangedFor(nameof(IsObservedGrabbing))]
    [NotifyPropertyChangedFor(nameof(IsObservedSnapping))]
    [NotifyPropertyChangedFor(nameof(IsObservedFrozen))]
    [NotifyPropertyChangedFor(nameof(IsRemoteOperatorActive))]
    [NotifyPropertyChangedFor(nameof(ObservedControlStateText))]
    private string _deviceStatus = XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartGrabCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyPropertyChangedFor(nameof(CanSelectSnapFrameMode))]
    private bool _isCapturing = false;

    // Device state can be observed by several clients, but only the client that
    // successfully acquired the command lease may enable acquisition controls.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartGrabCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnapCommand))]
    [NotifyPropertyChangedFor(nameof(CanSelectSnapFrameMode))]
    [NotifyPropertyChangedFor(nameof(IsRemoteOperatorActive))]
    [NotifyPropertyChangedFor(nameof(ObservedControlStateText))]
    private bool _hasCommandControl;

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
    public bool IsDeviceObservedConnected => DeviceStatus is
        nameof(XRaySystem.Domain.Entities.DeviceStatus.Connected) or
        nameof(XRaySystem.Domain.Entities.DeviceStatus.Grabbing) or
        nameof(XRaySystem.Domain.Entities.DeviceStatus.Snapping) or
        nameof(XRaySystem.Domain.Entities.DeviceStatus.Frozen);
    public bool IsObservedGrabbing =>
        DeviceStatus == nameof(XRaySystem.Domain.Entities.DeviceStatus.Grabbing);
    public bool IsObservedSnapping =>
        DeviceStatus == nameof(XRaySystem.Domain.Entities.DeviceStatus.Snapping);
    public bool IsObservedFrozen =>
        DeviceStatus == nameof(XRaySystem.Domain.Entities.DeviceStatus.Frozen);
    public bool IsRemoteOperatorActive => IsDeviceObservedConnected && !HasCommandControl;
    public string ObservedControlStateText => IsRemoteOperatorActive
        ? $"REMOTE OPERATOR · {DeviceStatus.ToUpperInvariant()}"
        : HasCommandControl
            ? $"DESKTOP CONTROL · {DeviceStatus.ToUpperInvariant()}"
            : DeviceStatus.ToUpperInvariant();

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
        WindowLevelRenderer windowLevelRenderer,
        IRemoteDeviceClient? remoteDeviceClient = null)
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
        _remoteDeviceClient = remoteDeviceClient;

        _sensorService.StatusChanged += OnDeviceStatusChanged;
        _sensorService.MeasurementReceived += QueueLatestMeasurement;
        if (_sensorService is IAcquisitionCoordinator coordinator)
            coordinator.HardwareStateChanged += OnHardwareStateChanged;
        if (_remoteDeviceClient is not null)
        {
            _remoteDeviceClient.FrameReadyReceived += OnRemoteFrameReady;
            _remoteDeviceClient.ConnectionStateChanged += OnRemoteConnectionStateChanged;
        }
        _frameFreshnessTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _frameFreshnessTimer.Tick += (_, _) => UpdateFrameFreshness();
        _frameFreshnessTimer.Start();
        ApplyAcquisitionParametersIfValid();
    }

    private void OnRemoteFrameReady(FrameReadyEventContract frame)
    {
        if (!RemoteObservationPolicy.ShouldAutoPresentCommittedSnap(
                HasCommandControl,
                frame.Operation,
                frame.ImageId))
            return;
        _pendingRemoteHistoryImageIds.Enqueue(frame.ImageId!.Value);
        ScheduleRemoteHistoryRefresh();
    }

    private void OnRemoteConnectionStateChanged(bool connected)
    {
        if (!connected) return;

        // SignalR can reconnect after one or more SNAP notifications were missed. Reconcile
        // against the authoritative DeviceHost catalog instead of assuming event delivery was
        // complete. The history lock coalesces this with an in-progress startup refresh.
        _ = RefreshHistoryAsync();
    }

    private void ScheduleRemoteHistoryRefresh()
    {
        if (Interlocked.CompareExchange(ref _remoteHistoryRefreshScheduled, 1, 0) != 0) return;
        _ = RefreshRemoteHistoryAfterCommitAsync();
    }

    private async Task RefreshRemoteHistoryAfterCommitAsync()
    {
        try
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (_remoteDeviceClient is null) return;
            var processed = new HashSet<Guid>();
            while (_pendingRemoteHistoryImageIds.TryDequeue(out var imageId))
            {
                if (!processed.Add(imageId)) continue;
                var metadata = await _remoteDeviceClient
                    .GetImageAsync(imageId)
                    .ConfigureAwait(false);
                var item = CreateRemoteHistoryDto(metadata);
                var displayItem = item;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var existing = ImageHistory.FirstOrDefault(candidate => candidate.Id == item.Id);
                    if (existing is not null &&
                        !string.IsNullOrWhiteSpace(existing.ImagePath) &&
                        File.Exists(existing.ImagePath))
                    {
                        displayItem = existing;
                        return;
                    }
                    if (existing is not null) ImageHistory.Remove(existing);
                    var insertAt = 0;
                    while (insertAt < ImageHistory.Count &&
                           ImageHistory[insertAt].Timestamp > item.Timestamp)
                        insertAt++;
                    ImageHistory.Insert(insertAt, item);
                });

                // A SNAP initiated by the mobile operator must replace an older
                // history review on the desktop. The desktop remains an observer:
                // displaying the frame does not grant it command control.
                if (RemoteObservationPolicy.ShouldAutoPresentCommittedSnap(
                        HasCommandControl,
                        metadata.Operation,
                        metadata.ImageId))
                    await DisplayRemoteSnapAsync(metadata, displayItem).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Remote history refresh error: {ex.Message}");
            try
            {
                // A transient failure after dequeuing an event must not leave the
                // desktop catalog stale. The full merge is slower, so it is used
                // only as a recovery path.
                await RefreshHistoryAsync().ConfigureAwait(false);
            }
            catch (Exception recoveryException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Remote history recovery refresh error: {recoveryException.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _remoteHistoryRefreshScheduled, 0);
            if (!_pendingRemoteHistoryImageIds.IsEmpty) ScheduleRemoteHistoryRefresh();
        }
    }

    private async Task DisplayRemoteSnapAsync(
        RemoteImageMetadataContract metadata,
        SensorDataDto item)
    {
        var capturedAtTicks = metadata.CapturedAtUtc.UtcDateTime.Ticks;
        if (capturedAtTicks <= Interlocked.Read(ref _lastAutoDisplayedRemoteSnapUtcTicks))
            return;

        var previewPath = await EnsureRemoteHistoryAvailableAsync(item).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(previewPath) || !File.Exists(previewPath))
            throw new FileNotFoundException(
                "The verified remote SNAP preview could not be created.",
                previewPath);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (HasCommandControl ||
                capturedAtTicks <= Interlocked.Read(ref _lastAutoDisplayedRemoteSnapUtcTicks))
                return;

            LoadHistoryTiff(previewPath, item);
            Interlocked.Exchange(ref _lastAutoDisplayedRemoteSnapUtcTicks, capturedAtTicks);
            ImageStatusBadgeText =
                $"REMOTE SNAP FROZEN: {item.Voltage:F0} kV · {item.PowerW:F0} W · {item.ExposureTime:F0} ms";
            ImageStatusBadgeColor = "#FFB020";
        });
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
                HasCommandControl = false;
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
        var forceLiveDisplay = _remoteDeviceClient is not null &&
                               measurement.Frame is { } frame &&
                               RemoteObservationPolicy.ShouldForceLivePresentation(frame.Operation);
        Interlocked.Exchange(ref _pendingLiveMeasurement,
            new PendingMeasurement(
                measurement,
                Volatile.Read(ref _sessionGeneration),
                forceLiveDisplay));
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
        {
            if (pending.ForceLiveDisplay)
            {
                // Remote GRAB is authoritative live activity. It intentionally
                // supersedes a locally opened history image without implying
                // that this desktop owns the command lease.
                IsPaused = false;
                var frame = pending.Measurement.Frame;
                ImageStatusBadgeText = frame is null
                    ? "REMOTE GRAB ACTIVE · LOW PREVIEW"
                    : $"REMOTE GRAB ACTIVE · {frame.Width}x{frame.Height} LOW PREVIEW";
                ImageStatusBadgeColor = "#32D74B";
            }

            ApplyMeasurement(
                pending.Measurement,
                pending.Generation,
                displayWhilePaused: pending.ForceLiveDisplay);
        }

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
        !IsConnecting && !HasCommandControl &&
        DeviceStatus == XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();
    private bool CanStartGrab() =>
        RemoteObservationPolicy.CanIssueAcquisitionCommand(HasCommandControl) &&
        !IsConnecting && !IsCapturing && !IsSnapping &&
        DeviceStatus is nameof(XRaySystem.Domain.Entities.DeviceStatus.Connected) or
            nameof(XRaySystem.Domain.Entities.DeviceStatus.Frozen);
    private bool CanStop() =>
        RemoteObservationPolicy.CanIssueAcquisitionCommand(HasCommandControl) && IsCapturing;
    private bool CanAbort() =>
        RemoteObservationPolicy.CanIssueAcquisitionCommand(HasCommandControl) && (IsCapturing || IsSnapping);
    private bool CanDisconnect() => RemoteObservationPolicy.CanIssueAcquisitionCommand(HasCommandControl) &&
                                    DeviceStatus != XRaySystem.Domain.Entities.DeviceStatus.Disconnected.ToString();
    private bool CanSnap() => RemoteObservationPolicy.CanIssueAcquisitionCommand(HasCommandControl) &&
                              DeviceStatus is nameof(XRaySystem.Domain.Entities.DeviceStatus.Connected) or
                                  nameof(XRaySystem.Domain.Entities.DeviceStatus.Grabbing) or
                                  nameof(XRaySystem.Domain.Entities.DeviceStatus.Frozen) &&
                              ExposureTimeIsValid && !IsSnapping;
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
    public bool CanSelectSnapFrameMode => HasCommandControl && !IsCapturing && !IsSnapping;

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
            SystemDialogWindow.ShowMessage(
                $"The database is not ready, so SNAP was not started:\n{ex.Message}",
                "Snap Unavailable",
                SystemDialogSeverity.Warning);
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

            ImageStatusBadgeText = $"SNAP FROZEN ({measurement.Frame.Width}x{measurement.Frame.Height} · 16-BIT TIFF - {savedFiles.FileStem})";

            var dto = _processTelemetryUseCase.Execute(measurement);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var existing = ImageHistory.FirstOrDefault(item => item.Id == dto.Id);
                if (existing is not null) ImageHistory.Remove(existing);
                ImageHistory.Insert(0, dto); // En yeni üstte
            });
        }
        catch (Exception ex)
        {
            // The captured frame remains visible even when TIFF/DB readiness or persistence fails.
            var preservedFileMessage = savedImagePath is not null && savedRawPath is not null &&
                                       File.Exists(savedImagePath) && File.Exists(savedRawPath)
                ? $"\n\nThe 16-bit TIFF source and preview were preserved, but the metadata record may be incomplete:\n{savedRawPath}"
                : string.Empty;
            SystemDialogWindow.ShowMessage(
                $"{ex.Message}{preservedFileMessage}",
                "Snap Error",
                SystemDialogSeverity.Error);
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
            SystemDialogWindow.ShowMessage(
                "Image file not found.",
                "Image Unavailable",
                SystemDialogSeverity.Warning);
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
            SystemDialogWindow.ShowMessage(
                $"Unable to load image: {ex.Message}",
                "Image Error",
                SystemDialogSeverity.Error);
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
        try
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
            ImageStatusBadgeText = "GRAB ACTIVE · LOW PREVIEW";
            ImageStatusBadgeColor = "#32D74B";
        }
        catch (Exception ex)
        {
            SystemDialogWindow.ShowMessage(
                ex.Message,
                "GRAB Failed",
                SystemDialogSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        try
        {
            IsConnecting = true;
            ClearRawSource();
            await _sensorService.ConnectAsync();
            HasCommandControl = true;
        }
        catch (Exception ex)
        {
            HasCommandControl = false;
            SystemDialogWindow.ShowMessage(
                ex.Message,
                "Sensor Connection Error",
                SystemDialogSeverity.Error);
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
        try
        {
            await _sensorService.StopCaptureAsync();
            ClearLiveRawSource();
        }
        catch (Exception ex)
        {
            SystemDialogWindow.ShowMessage(
                ex.Message,
                "Freeze Failed",
                SystemDialogSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortAsync()
    {
        Interlocked.Increment(ref _sessionGeneration);
        _windowLevelRenderer.Invalidate();
        try
        {
            await _sensorService.AbortAsync();
            ClearLiveRawSource();
            IsPaused = false;
        }
        catch (Exception ex)
        {
            SystemDialogWindow.ShowMessage(
                ex.Message,
                "Stop Failed",
                SystemDialogSeverity.Error);
        }
    }
    
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        Interlocked.Increment(ref _sessionGeneration);
        _windowLevelRenderer.Invalidate();
        try
        {
            await _sensorService.DisconnectAsync();
            ClearLiveRawSource();
            HasCommandControl = false;
        }
        catch (Exception ex)
        {
            SystemDialogWindow.ShowMessage(
                ex.Message,
                "Disconnect Failed",
                SystemDialogSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        await _historyLoadLock.WaitAsync();
        try
        {
            await App.DatabaseReady;
            var localHistory = await _getHistoryUseCase.ExecuteAsync(maximumCount: 500);
            IReadOnlyList<RemoteImageMetadataContract> remoteHistory = [];
            if (_remoteDeviceClient is not null)
            {
                try
                {
                    // History observation is read-only and must not require taking the device
                    // command lease. This keeps the desktop catalog live while a mobile operator
                    // owns acquisition control.
                    await _remoteDeviceClient.ValidateProtocolAsync().ConfigureAwait(false);
                    await _remoteDeviceClient.ConnectEventsAsync().ConfigureAwait(false);
                    remoteHistory = await _remoteDeviceClient.GetImagesAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Remote catalog load error: {ex.Message}");
                }
            }

            var merged = remoteHistory
                .Select(CreateRemoteHistoryDto)
                .ToDictionary(item => item.Id);
            foreach (var local in localHistory)
                merged[local.Id] = local;
            var history = merged.Values
                .OrderByDescending(item => item.Timestamp)
                .ToArray();
            
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ImageHistory.Clear();
                foreach (var item in history)
                {
                    if (!string.IsNullOrWhiteSpace(item.ImagePath) || _remoteDeviceClient is not null)
                        ImageHistory.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshHistoryAsync error: {ex.Message}");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                SystemDialogWindow.ShowMessage(
                    $"Unable to load history records:\n{ex.Message}",
                    "History Load Error",
                    SystemDialogSeverity.Warning));
        }
        finally
        {
            _historyLoadLock.Release();
        }
    }

    public async Task<string?> EnsureRemoteHistoryAvailableAsync(SensorDataDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!string.IsNullOrWhiteSpace(item.ImagePath) && File.Exists(item.ImagePath) &&
            !string.IsNullOrWhiteSpace(item.RawImagePath) && File.Exists(item.RawImagePath))
            return item.ImagePath;
        if (_remoteDeviceClient is null)
            return null;

        var rawBytes = await _remoteDeviceClient.GetRawImageAsync(item.Id).ConfigureAwait(false);
        RawGray16Frame frame;
        using (var stream = new MemoryStream(rawBytes, writable: false))
            frame = Gray16RawFileCodec.Read(stream);
        if (frame.AcquisitionId != item.AcquisitionId ||
            frame.Width != item.SourceWidth || frame.Height != item.SourceHeight)
            throw new InvalidDataException("Remote history Gray16 metadata does not match the catalog entry.");

        var requested = item.RequestedParameters ?? new AcquisitionParameters(
            item.Voltage,
            item.PowerW,
            item.ExposureTime);
        requested.EnsureValid();
        var tubeCurrent = item.Voltage > 0 ? item.PowerW / item.Voltage : 0;
        var measurement = new XRayMeasurement(
            item.Id,
            item.Timestamp.ToUniversalTime(),
            new SensorReadings(
                tubeCurrent,
                item.Voltage,
                item.XRayIntensity,
                item.ExposureTime,
                item.TubeCurrentProvenance,
                item.VoltageProvenance,
                item.ExposureTimeProvenance),
            frame,
            item.IsSimulator,
            requested);

        var bitmap = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var source = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                System.Windows.Media.PixelFormats.Gray16,
                null,
                frame.Pixels,
                frame.StrideBytes);
            source.Freeze();
            return source;
        });
        var fileStem = AcquisitionFileNameFactory.CreateStem(measurement);
        var saved = await Task.Run(() =>
            _imageStorageService.SaveSnapshot(bitmap, frame, fileStem)).ConfigureAwait(false);
        measurement.SetImagePath(saved.PreviewPath);
        measurement.SetRawImagePath(saved.RawPath);
        try
        {
            await _saveMeasurementUseCase.ExecuteIfMissingAsync(measurement).ConfigureAwait(false);
        }
        catch
        {
            // A simultaneous local SNAP may have inserted the same measurement.
            // The verified files remain usable and the next refresh resolves the local row.
        }

        item.ImagePath = saved.PreviewPath;
        item.RawImagePath = saved.RawPath;
        return saved.PreviewPath;
    }

    private static SensorDataDto CreateRemoteHistoryDto(RemoteImageMetadataContract metadata)
    {
        var profile = Enum.TryParse<AcquisitionProfile>(metadata.Profile, true, out var parsedProfile) &&
                      Enum.IsDefined(parsedProfile)
            ? parsedProfile
            : AcquisitionProfile.IndustrialQaPhantom;
        return new SensorDataDto
        {
            Id = metadata.ImageId,
            Timestamp = metadata.CapturedAtUtc.LocalDateTime,
            PowerW = metadata.MeasuredTelemetry.PowerW,
            Voltage = metadata.MeasuredTelemetry.VoltageKv,
            XRayIntensity = metadata.MeasuredTelemetry.XRayIntensity,
            ExposureTime = metadata.MeasuredTelemetry.ExposureTimeMs,
            Profile = profile,
            IsSimulator = metadata.IsSimulator == true,
            RequestedParameters = new AcquisitionParameters(
                metadata.RequestedParameters.VoltageKv,
                metadata.RequestedParameters.PowerW,
                metadata.RequestedParameters.ExposureTimeMs),
            AcquisitionId = metadata.AcquisitionId,
            Operation = metadata.Operation == AcquisitionOperationContract.Snap
                ? AcquisitionOperation.Snap
                : AcquisitionOperation.Grab,
            SnapFrameMode = metadata.SnapFrameMode == SnapFrameModeContract.Low
                ? SnapFrameMode.Low
                : SnapFrameMode.Raw,
            SourceWidth = metadata.Width,
            SourceHeight = metadata.Height,
            SourceMarker = metadata.IsSimulator switch
            {
                true => "remote-device-catalog:simulation",
                false => "remote-device-catalog:device",
                null => null
            },
            VoltageProvenance = ToDomain(metadata.MeasuredTelemetry.VoltageProvenance),
            TubeCurrentProvenance = ToDomain(metadata.MeasuredTelemetry.TubeCurrentProvenance),
            ExposureTimeProvenance = ToDomain(metadata.MeasuredTelemetry.ExposureTimeProvenance)
        };
    }

    private static MeasurementProvenance ToDomain(MeasurementProvenanceContract provenance) => provenance switch
    {
        MeasurementProvenanceContract.Measured => MeasurementProvenance.Measured,
        MeasurementProvenanceContract.AppliedTarget => MeasurementProvenance.AppliedTarget,
        MeasurementProvenanceContract.Derived => MeasurementProvenance.Derived,
        _ => MeasurementProvenance.Unknown
    };
}
