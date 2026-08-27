using Microsoft.Maui.Controls;
using XRaySystem.Mobile.Imaging;
using XRaySystem.Mobile.ViewModels;

namespace XRaySystem.Mobile;

public partial class MainPage : ContentPage
{
    private const double MinimumImageScale = 1.0;
    // Kullanıcıya görünür üst limit yoktur; yalnız platform transform taşmasını engeller.
    private const double NumericSafetyImageScale = 1_000_000.0;
    private const double ZoomFactor = 1.25;
    private const double CoordinateFocusScale = 8.0;
    private const double ReturnToFitSnapScale = 1.02;

    private readonly MainViewModel _viewModel;
    private readonly PixelGridDrawable _fullScreenPixelGridDrawable = new();
    private double _pinchStartScale = MinimumImageScale;
    private double _panStartX;
    private double _panStartY;
    private double _fullScreenPinchStartScale = MinimumImageScale;
    private double _fullScreenPanStartX;
    private double _fullScreenPanStartY;
    private bool _deviceControlTransitionRunning;
#if ANDROID
    private const string AndroidPresentationFailureMessage =
        "The new GRAB frame could not be displayed; the latest valid image is preserved.";

    private sealed record AndroidPresentationFrame(
        byte[] BmpBytes,
        int Width,
        int Height,
        long Version);

    private readonly object _androidPresentationGate = new();
    private Android.Widget.ImageView? _androidImageView;
    private Android.Widget.ImageView? _androidFullScreenImageView;
    private Android.Views.View? _androidFullScreenViewportView;
    private float _androidLastPanX;
    private float _androidLastPanY;
    private bool _androidIsPinching;
    private float _androidPinchStartDistance;
    private double _androidPinchStartScale;
    private double _androidPinchAnchorX;
    private double _androidPinchAnchorY;
    private Android.Graphics.Bitmap? _androidDisplayedBitmap;
    private AndroidX.RecyclerView.Widget.RecyclerView? _androidHistoryRecyclerView;
    private AndroidPresentationFrame? _androidPendingPresentationFrame;
    private bool _androidPresentationWorkerRunning;
    private bool _androidPresentationStopping;
    private long _androidLastPresentedVersion;
    private long _androidPresentationVersion;
#endif

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        // Keep a single control surface instance so command bindings and
        // AutomationId values are not duplicated between the two workspaces.
        ControlPanel.Children.Remove(DeviceControlCard);
        Grid.SetRow(DeviceControlCard, 0);
        DeviceControlContentHost.Children.Add(DeviceControlCard);
        FullScreenPixelGrid.Drawable = _fullScreenPixelGridDrawable;
        SensorImage.GestureRecognizers.Remove(ImagePanGesture);
        _viewModel = viewModel;
        BindingContext = viewModel;
        _viewModel.ReviewImageOpened += OnReviewImageOpened;
        _viewModel.DialogRequested += OnDialogRequested;
#if WINDOWS
        PageScrollView.HandlerChanged += OnPageScrollViewHandlerChanged;
        ConfigureWindowsScrollViewer();
#endif
#if ANDROID
        // Android'in asenkron ImageSource/Glide değişimi eski drawable'ı yeni kare hazır
        // olmadan temizleyebilir. Native presenter son geçerli bitmap'i atomik değiştirir.
        SensorImage.RemoveBinding(Image.SourceProperty);
        FullScreenSensorImage.RemoveBinding(Image.SourceProperty);
        SensorImage.Source = null;
        FullScreenSensorImage.Source = null;
        _viewModel.PresentationFrameReady += OnAndroidPresentationFrameReady;
        FullScreenSensorImage.GestureRecognizers.Remove(FullScreenPinchGesture);
        FullScreenSensorImage.GestureRecognizers.Remove(FullScreenPanGesture);
        SensorImage.HandlerChanged += OnSensorImageHandlerChanged;
        FullScreenSensorImage.HandlerChanged += OnFullScreenSensorImageHandlerChanged;
        FullScreenImageViewport.HandlerChanged += OnFullScreenImageViewportHandlerChanged;
        HistoryCollectionView.HandlerChanged += OnHistoryCollectionViewHandlerChanged;
        ConfigureAndroidImageGestures();
        ConfigureAndroidFullScreenViewport();
        ConfigureAndroidHistoryScrolling();
#endif
    }

    private void OnOpenFullScreenTapped(object? sender, TappedEventArgs e)
        => OpenFullScreenViewer();

    private void OnOpenFullScreenClicked(object? sender, EventArgs e)
        => OpenFullScreenViewer();

    private async void OnOpenDeviceControlClicked(object? sender, EventArgs e) =>
        await OpenDeviceControlAsync();

    private async void OnOpenDeviceControlSwiped(object? sender, SwipedEventArgs e)
    {
        // Image pan owns horizontal movement while zoomed; do not turn a review
        // gesture into an accidental navigation action.
        if (SensorImage.Scale > MinimumImageScale || FullScreenViewerOverlay.IsVisible)
            return;
        await OpenDeviceControlAsync();
    }

    private async void OnCloseDeviceControlClicked(object? sender, EventArgs e) =>
        await CloseDeviceControlAsync();

    private async void OnCloseDeviceControlSwiped(object? sender, SwipedEventArgs e) =>
        await CloseDeviceControlAsync();

    private async Task OpenDeviceControlAsync()
    {
        if (_deviceControlTransitionRunning || DeviceControlPage.IsVisible)
            return;

        _deviceControlTransitionRunning = true;
        try
        {
            var pageWidth = Width > 0 ? Width : 420;
            DeviceControlPage.TranslationX = pageWidth;
            DeviceControlPage.IsVisible = true;
            PageScrollView.InputTransparent = true;
            await DeviceControlScrollView.ScrollToAsync(0, 0, animated: false);
            await DeviceControlPage.TranslateTo(0, 0, 220, Easing.CubicOut);
            SemanticScreenReader.Announce("Device control");
        }
        finally
        {
            _deviceControlTransitionRunning = false;
        }
    }

    private async Task CloseDeviceControlAsync()
    {
        if (_deviceControlTransitionRunning || !DeviceControlPage.IsVisible)
            return;

        _deviceControlTransitionRunning = true;
        try
        {
            var pageWidth = Width > 0 ? Width : 420;
            await DeviceControlPage.TranslateTo(pageWidth, 0, 200, Easing.CubicIn);
            SemanticScreenReader.Announce("Imaging workspace");
        }
        finally
        {
            // Navigation animation is cosmetic; its interruption must never
            // leave the imaging workspace blocked behind an input shield.
            DeviceControlPage.IsVisible = false;
            DeviceControlPage.TranslationX = 0;
            PageScrollView.InputTransparent = false;
            _deviceControlTransitionRunning = false;
        }
    }

    private void OpenFullScreenViewer()
    {
        if (!_viewModel.HasCurrentImage)
            return;

        ResetFullScreenImageTransform();
        FullScreenViewerOverlay.IsVisible = true;
        PageScrollView.IsEnabled = false;
        if (_viewModel.CurrentFrameWidth > 0 && _viewModel.CurrentFrameHeight > 0)
        {
            FullScreenCoordinateXEntry.Text = (_viewModel.CurrentFrameWidth / 2).ToString();
            FullScreenCoordinateYEntry.Text = (_viewModel.CurrentFrameHeight / 2).ToString();
        }
        Dispatcher.Dispatch(() =>
        {
            UpdateFullScreenCoordinateReadout();
            UpdateFullScreenPixelGrid();
        });
#if ANDROID
        ConfigureAndroidPixelRendering(_androidFullScreenImageView);
#endif
    }

    private void OnCloseFullScreenClicked(object? sender, EventArgs e) => CloseFullScreenViewer();

    private void OnDialogRequested(MobileDialogRequest request)
    {
        Dispatcher.Dispatch(() =>
        {
            var accent = request.Severity == MobileDialogSeverity.Warning
                ? Color.FromArgb("#FFB020")
                : Color.FromArgb("#FF4D5E");
            AppDialogAccent.Color = accent;
            AppDialogSeverityLabel.Text = request.Severity == MobileDialogSeverity.Warning
                ? "ATTENTION"
                : "ACTION REQUIRED";
            AppDialogSeverityLabel.TextColor = accent;
            AppDialogTitleLabel.Text = request.Title;
            AppDialogMessageLabel.Text = request.Message;
            AppDialogOverlay.IsVisible = true;
        });
    }

    private void OnAppDialogDismissed(object? sender, EventArgs e) =>
        AppDialogOverlay.IsVisible = false;

    private void CloseFullScreenViewer()
    {
        FullScreenViewerOverlay.IsVisible = false;
        PageScrollView.IsEnabled = true;
        ResetFullScreenImageTransform();
    }

    private void OnImagePinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _pinchStartScale = SensorImage.Scale;
                break;
            case GestureStatus.Running:
                SetImageScale(_pinchStartScale * e.Scale);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ClampImageTranslation();
                break;
        }
    }

    private void OnImagePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (SensorImage.Scale <= MinimumImageScale)
            return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = SensorImage.TranslationX;
                _panStartY = SensorImage.TranslationY;
                break;
            case GestureStatus.Running:
                SensorImage.TranslationX = _panStartX + e.TotalX;
                SensorImage.TranslationY = _panStartY + e.TotalY;
                ClampImageTranslation();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ClampImageTranslation();
                break;
        }
    }

    private void OnZoomOutClicked(object? sender, EventArgs e) =>
        SetImageScale(SensorImage.Scale / ZoomFactor);

    private void OnZoomInClicked(object? sender, EventArgs e) =>
        SetImageScale(SensorImage.Scale * ZoomFactor);

    private void OnResetZoomClicked(object? sender, EventArgs e) => ResetImageTransform();

    private void OnFullScreenImagePinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _fullScreenPinchStartScale = FullScreenSensorImage.Scale;
                break;
            case GestureStatus.Running:
                SetFullScreenImageScale(
                    _fullScreenPinchStartScale * e.Scale,
                    new Point(
                        e.ScaleOrigin.X * FullScreenSensorImage.Width,
                        e.ScaleOrigin.Y * FullScreenSensorImage.Height));
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ClampFullScreenImageTranslation();
                break;
        }
    }

    private void OnFullScreenImagePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (FullScreenSensorImage.Scale <= MinimumImageScale)
            return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _fullScreenPanStartX = FullScreenSensorImage.TranslationX;
                _fullScreenPanStartY = FullScreenSensorImage.TranslationY;
                break;
            case GestureStatus.Running:
                FullScreenSensorImage.TranslationX = _fullScreenPanStartX + e.TotalX;
                FullScreenSensorImage.TranslationY = _fullScreenPanStartY + e.TotalY;
                ClampFullScreenImageTranslation();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ClampFullScreenImageTranslation();
                break;
        }
    }

    private void OnFullScreenZoomOutClicked(object? sender, EventArgs e) =>
        SetFullScreenImageScale(FullScreenSensorImage.Scale / ZoomFactor);

    private void OnFullScreenZoomInClicked(object? sender, EventArgs e) =>
        SetFullScreenImageScale(FullScreenSensorImage.Scale * ZoomFactor);

    private void OnFullScreenResetZoomClicked(object? sender, EventArgs e) =>
        ResetFullScreenImageTransform();

    private void OnFullScreenImageSizeChanged(object? sender, EventArgs e)
    {
        UpdateFullScreenCoordinateReadout();
        UpdateFullScreenPixelGrid();
    }

    private async void OnReviewImageOpened()
    {
        if (DeviceControlPage.IsVisible)
            await CloseDeviceControlAsync();
        ResetImageTransform();
        await PageScrollView.ScrollToAsync(ViewerPanel, ScrollToPosition.Start, animated: true);
    }

    private void SetImageScale(double requestedScale)
    {
        SensorImage.Scale = NormalizeImageScale(requestedScale);
        var panEnabled = SensorImage.Scale > MinimumImageScale;
        if (panEnabled && !SensorImage.GestureRecognizers.Contains(ImagePanGesture))
            SensorImage.GestureRecognizers.Add(ImagePanGesture);
        else if (!panEnabled && SensorImage.GestureRecognizers.Contains(ImagePanGesture))
            SensorImage.GestureRecognizers.Remove(ImagePanGesture);

        if (!panEnabled)
        {
            SensorImage.TranslationX = 0;
            SensorImage.TranslationY = 0;
        }
        else
        {
            ClampImageTranslation();
        }

        ZoomLevelLabel.Text = $"{Math.Round(SensorImage.Scale * 100):F0}%";
    }

    private void SetFullScreenImageScale(double requestedScale, Point? focusPoint = null)
    {
        var oldScale = Math.Max(MinimumImageScale, FullScreenSensorImage.Scale);
        var newScale = NormalizeImageScale(requestedScale);
        var translationX = FullScreenSensorImage.TranslationX;
        var translationY = FullScreenSensorImage.TranslationY;
        if (focusPoint is Point focus &&
            FullScreenSensorImage.Width > 0 && FullScreenSensorImage.Height > 0 &&
            Math.Abs(newScale - oldScale) > double.Epsilon)
        {
            var originX = FullScreenSensorImage.Width / 2.0;
            var originY = FullScreenSensorImage.Height / 2.0;
            var localX = (focus.X - originX - FullScreenSensorImage.TranslationX) / oldScale + originX;
            var localY = (focus.Y - originY - FullScreenSensorImage.TranslationY) / oldScale + originY;
            translationX =
                focus.X - originX - (localX - originX) * newScale;
            translationY =
                focus.Y - originY - (localY - originY) * newScale;
        }

        ApplyFullScreenImageTransform(newScale, translationX, translationY);
    }

    private void ApplyFullScreenImageTransform(
        double requestedScale,
        double requestedTranslationX,
        double requestedTranslationY)
    {
        var newScale = NormalizeImageScale(requestedScale);
        if (newScale <= ReturnToFitSnapScale)
            newScale = MinimumImageScale;

        FullScreenSensorImage.Scale = newScale;
        if (newScale <= MinimumImageScale)
        {
            FullScreenSensorImage.TranslationX = 0;
            FullScreenSensorImage.TranslationY = 0;
        }
        else
        {
            FullScreenSensorImage.TranslationX = double.IsFinite(requestedTranslationX)
                ? requestedTranslationX
                : 0;
            FullScreenSensorImage.TranslationY = double.IsFinite(requestedTranslationY)
                ? requestedTranslationY
                : 0;
            ClampFullScreenImageTranslation();
        }

        FullScreenZoomLevelLabel.Text = $"{Math.Round(newScale * 100):F0}%";
        UpdateFullScreenCoordinateReadout();
        UpdateFullScreenPixelGrid();
    }

    private static double NormalizeImageScale(double requestedScale)
    {
        if (!double.IsFinite(requestedScale))
            return NumericSafetyImageScale;
        return Math.Clamp(requestedScale, MinimumImageScale, NumericSafetyImageScale);
    }

    private void ResetImageTransform()
    {
        SensorImage.Scale = MinimumImageScale;
        SensorImage.TranslationX = 0;
        SensorImage.TranslationY = 0;
        SensorImage.GestureRecognizers.Remove(ImagePanGesture);
        ZoomLevelLabel.Text = "100%";
    }

    private void ResetFullScreenImageTransform()
    {
#if ANDROID
        _androidIsPinching = false;
#endif
        FullScreenSensorImage.Scale = MinimumImageScale;
        FullScreenSensorImage.TranslationX = 0;
        FullScreenSensorImage.TranslationY = 0;
        FullScreenZoomLevelLabel.Text = "100%";
        UpdateFullScreenCoordinateReadout();
        UpdateFullScreenPixelGrid();
    }

    private void ClampImageTranslation()
    {
        var (maximumX, maximumY) = GetAspectFitTranslationBounds(
            SensorImage.Width,
            SensorImage.Height,
            SensorImage.Scale);
        SensorImage.TranslationX = Math.Clamp(SensorImage.TranslationX, -maximumX, maximumX);
        SensorImage.TranslationY = Math.Clamp(SensorImage.TranslationY, -maximumY, maximumY);
    }

    private void ClampFullScreenImageTranslation()
    {
        var (maximumX, maximumY) = GetAspectFitTranslationBounds(
            FullScreenSensorImage.Width,
            FullScreenSensorImage.Height,
            FullScreenSensorImage.Scale);
        FullScreenSensorImage.TranslationX =
            Math.Clamp(FullScreenSensorImage.TranslationX, -maximumX, maximumX);
        FullScreenSensorImage.TranslationY =
            Math.Clamp(FullScreenSensorImage.TranslationY, -maximumY, maximumY);
        UpdateFullScreenCoordinateReadout();
        UpdateFullScreenPixelGrid();
    }

    private (double MaximumX, double MaximumY) GetAspectFitTranslationBounds(
        double viewportWidth,
        double viewportHeight,
        double scale)
    {
        var pixelWidth = _viewModel.CurrentFrameWidth;
        var pixelHeight = _viewModel.CurrentFrameHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0)
            return (0, 0);

        var fitScale = Math.Min(viewportWidth / pixelWidth, viewportHeight / pixelHeight);
        var scaledContentWidth = pixelWidth * fitScale * scale;
        var scaledContentHeight = pixelHeight * fitScale * scale;
        return (
            Math.Max(0, (scaledContentWidth - viewportWidth) / 2.0),
            Math.Max(0, (scaledContentHeight - viewportHeight) / 2.0));
    }

    private void OnFullScreenFocusCoordinateClicked(object? sender, EventArgs e)
    {
        var width = _viewModel.CurrentFrameWidth;
        var height = _viewModel.CurrentFrameHeight;
        if (width <= 0 || height <= 0 ||
            !int.TryParse(FullScreenCoordinateXEntry.Text, out var pixelX) ||
            !int.TryParse(FullScreenCoordinateYEntry.Text, out var pixelY) ||
            pixelX < 0 || pixelX >= width || pixelY < 0 || pixelY >= height)
        {
            FullScreenCenterCoordinateLabel.Text = width > 0 && height > 0
                ? $"INVALID · X: 0–{width - 1}  Y: 0–{height - 1}"
                : "IMAGE DIMENSIONS UNAVAILABLE";
            return;
        }

        FocusFullScreenCoordinate(pixelX, pixelY);
    }

    private void FocusFullScreenCoordinate(int pixelX, int pixelY)
    {
        var pixelWidth = _viewModel.CurrentFrameWidth;
        var pixelHeight = _viewModel.CurrentFrameHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0 ||
            FullScreenSensorImage.Width <= 0 || FullScreenSensorImage.Height <= 0)
            return;

        var fitScale = Math.Min(
            FullScreenSensorImage.Width / pixelWidth,
            FullScreenSensorImage.Height / pixelHeight);
        var renderedWidth = pixelWidth * fitScale;
        var renderedHeight = pixelHeight * fitScale;
        var offsetX = (FullScreenSensorImage.Width - renderedWidth) / 2.0;
        var offsetY = (FullScreenSensorImage.Height - renderedHeight) / 2.0;
        var localX = offsetX + (pixelX + 0.5) * fitScale;
        var localY = offsetY + (pixelY + 0.5) * fitScale;
        var targetScale = Math.Max(CoordinateFocusScale, FullScreenSensorImage.Scale);
        FullScreenSensorImage.Scale = NormalizeImageScale(targetScale);
        var originX = FullScreenSensorImage.Width / 2.0;
        var originY = FullScreenSensorImage.Height / 2.0;
        // Koordinat tam merkeze gelsin; kenar piksellerinde siyah alan görünmesi bilinçlidir.
        FullScreenSensorImage.TranslationX = -(localX - originX) * FullScreenSensorImage.Scale;
        FullScreenSensorImage.TranslationY = -(localY - originY) * FullScreenSensorImage.Scale;
        FullScreenZoomLevelLabel.Text = $"{Math.Round(FullScreenSensorImage.Scale * 100):F0}%";
        UpdateFullScreenCoordinateReadout();
        UpdateFullScreenPixelGrid();
    }

    private bool TryFullScreenViewportToImagePixel(Point viewportPoint, out int pixelX, out int pixelY)
    {
        pixelX = 0;
        pixelY = 0;
        var pixelWidth = _viewModel.CurrentFrameWidth;
        var pixelHeight = _viewModel.CurrentFrameHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0 ||
            FullScreenSensorImage.Width <= 0 || FullScreenSensorImage.Height <= 0)
            return false;

        var scale = Math.Max(MinimumImageScale, FullScreenSensorImage.Scale);
        var originX = FullScreenSensorImage.Width / 2.0;
        var originY = FullScreenSensorImage.Height / 2.0;
        var localX = (viewportPoint.X - originX - FullScreenSensorImage.TranslationX) / scale + originX;
        var localY = (viewportPoint.Y - originY - FullScreenSensorImage.TranslationY) / scale + originY;
        var fitScale = Math.Min(
            FullScreenSensorImage.Width / pixelWidth,
            FullScreenSensorImage.Height / pixelHeight);
        var renderedWidth = pixelWidth * fitScale;
        var renderedHeight = pixelHeight * fitScale;
        var offsetX = (FullScreenSensorImage.Width - renderedWidth) / 2.0;
        var offsetY = (FullScreenSensorImage.Height - renderedHeight) / 2.0;
        var sourceX = (localX - offsetX) / fitScale;
        var sourceY = (localY - offsetY) / fitScale;
        if (sourceX < 0 || sourceY < 0 || sourceX >= pixelWidth || sourceY >= pixelHeight)
            return false;

        pixelX = Math.Clamp((int)Math.Floor(sourceX), 0, pixelWidth - 1);
        pixelY = Math.Clamp((int)Math.Floor(sourceY), 0, pixelHeight - 1);
        return true;
    }

    private void UpdateFullScreenCoordinateReadout()
    {
        if (FullScreenCenterCoordinateLabel is null)
            return;

        var center = new Point(
            FullScreenSensorImage.Width / 2.0,
            FullScreenSensorImage.Height / 2.0);
        FullScreenCenterCoordinateLabel.Text =
            TryFullScreenViewportToImagePixel(center, out var x, out var y)
                ? $"CENTER X: {x}  Y: {y}"
                : "CENTER X: —  Y: —";
    }

    private void UpdateFullScreenPixelGrid()
    {
        if (FullScreenPixelGrid is null)
            return;

#if ANDROID
        if (!AndroidDecodedBitmapMatchesFrame())
        {
            FullScreenPixelGrid.IsVisible = false;
            return;
        }
#endif

        var isVisible = _fullScreenPixelGridDrawable.Configure(
            _viewModel.CurrentFrameWidth,
            _viewModel.CurrentFrameHeight,
            FullScreenSensorImage.Width,
            FullScreenSensorImage.Height,
            FullScreenSensorImage.Scale,
            FullScreenSensorImage.TranslationX,
            FullScreenSensorImage.TranslationY);
        FullScreenPixelGrid.IsVisible = isVisible;
        if (isVisible)
            FullScreenPixelGrid.Invalidate();
    }

#if ANDROID
    private bool AndroidDecodedBitmapMatchesFrame()
    {
        var imageView = _androidFullScreenImageView ??
            FullScreenSensorImage.Handler?.PlatformView as Android.Widget.ImageView;
        if (imageView?.Drawable is not Android.Graphics.Drawables.BitmapDrawable bitmapDrawable ||
            bitmapDrawable.Bitmap is not { } bitmap)
            return false;

        return bitmap.Width == _viewModel.CurrentFrameWidth &&
               bitmap.Height == _viewModel.CurrentFrameHeight;
    }

    private void OnAndroidPresentationFrameReady(byte[] bmpBytes, int width, int height)
    {
        var version = Interlocked.Increment(ref _androidPresentationVersion);
        var startWorker = false;
        lock (_androidPresentationGate)
        {
            if (_androidPresentationStopping)
                return;

            // Bounded latest-frame buffer: geciken kareler birikmez. Üretici hızlıysa
            // bekleyen kare atomik olarak yenisiyle değiştirilir.
            _androidPendingPresentationFrame =
                new AndroidPresentationFrame(bmpBytes, width, height, version);
            if (!_androidPresentationWorkerRunning)
            {
                _androidPresentationWorkerRunning = true;
                startWorker = true;
            }
        }

        if (startWorker)
            _ = Task.Run(ProcessAndroidPresentationFrames);
    }

    private void ProcessAndroidPresentationFrames()
    {
        while (true)
        {
            AndroidPresentationFrame frame;
            lock (_androidPresentationGate)
            {
                if (_androidPendingPresentationFrame is null)
                {
                    _androidPresentationWorkerRunning = false;
                    return;
                }

                frame = _androidPendingPresentationFrame;
                _androidPendingPresentationFrame = null;
            }

            DecodeAndPresentAndroidFrame(
                frame.BmpBytes,
                frame.Width,
                frame.Height,
                frame.Version);
        }
    }

    private void DecodeAndPresentAndroidFrame(byte[] bmpBytes, int width, int height, long version)
    {
        Android.Graphics.Bitmap? bitmap = null;
        try
        {
            using var options = new Android.Graphics.BitmapFactory.Options
            {
                InScaled = false,
                InSampleSize = 1
            };
            bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(
                bmpBytes,
                0,
                bmpBytes.Length,
                options);
            if (bitmap is null || bitmap.Width != width || bitmap.Height != height)
            {
                bitmap?.Dispose();
                ReportAndroidPresentationFailure(version);
                return;
            }

            lock (_androidPresentationGate)
            {
                if (_androidPresentationStopping)
                {
                    bitmap.Dispose();
                    bitmap = null;
                    return;
                }
            }

            var readyBitmap = bitmap;
            bitmap = null;
            // İşçi UI teslimi tamamlanana kadar ilerlemez. Böylece hem encoded-frame
            // yuvası hem de UI teslim kuyruğu sabit boyutlu kalır.
            try
            {
                MainThread.InvokeOnMainThreadAsync(() =>
                        PresentDecodedAndroidFrame(readyBitmap, version))
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                readyBitmap.Dispose();
                ReportAndroidPresentationFailure(version);
            }
        }
        catch
        {
            bitmap?.Dispose();
            ReportAndroidPresentationFailure(version);
        }
    }

    private void PresentDecodedAndroidFrame(Android.Graphics.Bitmap bitmap, long version)
    {
        if (version <= Volatile.Read(ref _androidLastPresentedVersion))
        {
            bitmap.Dispose();
            return;
        }

        var previous = _androidDisplayedBitmap;
        try
        {
            ApplyAndroidBitmap(_androidImageView, bitmap);
            ApplyAndroidBitmap(_androidFullScreenImageView, bitmap);
        }
        catch
        {
            // İki viewer'dan biri güncellenemezse diğerini de önceki doğrulanmış
            // bitmap'e döndür; yarım kare değişimi bırakma.
            TryRestoreAndroidBitmap(_androidImageView, previous);
            TryRestoreAndroidBitmap(_androidFullScreenImageView, previous);
            throw;
        }

        _androidDisplayedBitmap = bitmap;
        Volatile.Write(ref _androidLastPresentedVersion, version);
        previous?.Dispose();
        if (_viewModel.StatusMessage == AndroidPresentationFailureMessage)
            _viewModel.StatusMessage = _viewModel.Status == XRaySystem.Domain.Entities.DeviceStatus.Grabbing
                ? "GRAB active · latest-frame mode enabled."
                : "Image presentation refreshed.";
        UpdateFullScreenPixelGrid();
    }

    private void ReportAndroidPresentationFailure(long version)
    {
        try
        {
            MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (Volatile.Read(ref _androidLastPresentedVersion) < version)
                        _viewModel.StatusMessage = AndroidPresentationFailureMessage;
                })
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Window kapanıyorsa UI dispatcher artık teslim alamayabilir. Son bitmap
            // değiştirilmediği için burada ek bir boş/yanlış görüntü sunulmaz.
        }
    }

    private void ApplyAndroidDisplayedBitmap(Android.Widget.ImageView? imageView)
    {
        if (imageView is null || _androidDisplayedBitmap is null)
            return;

        ApplyAndroidBitmap(imageView, _androidDisplayedBitmap);
    }

    private static void ApplyAndroidBitmap(
        Android.Widget.ImageView? imageView,
        Android.Graphics.Bitmap bitmap)
    {
        if (imageView is null)
            return;

        imageView.SetImageBitmap(bitmap);
        ConfigureAndroidPixelRendering(imageView);
    }

    private static void TryRestoreAndroidBitmap(
        Android.Widget.ImageView? imageView,
        Android.Graphics.Bitmap? bitmap)
    {
        try
        {
            if (imageView is null)
                return;

            if (bitmap is null)
                imageView.SetImageDrawable(null);
            else
                ApplyAndroidBitmap(imageView, bitmap);
        }
        catch
        {
            // Presenter hatası raporlanır; kurtarma denemesi ana hatayı gölgelememeli.
        }
    }

    private void OnSensorImageHandlerChanged(object? sender, EventArgs e) =>
        ConfigureAndroidImageGestures();

    private void OnFullScreenSensorImageHandlerChanged(object? sender, EventArgs e)
    {
        _androidFullScreenImageView =
            FullScreenSensorImage.Handler?.PlatformView as Android.Widget.ImageView;
        ApplyAndroidDisplayedBitmap(_androidFullScreenImageView);
    }

    private void OnFullScreenImageViewportHandlerChanged(object? sender, EventArgs e) =>
        ConfigureAndroidFullScreenViewport();

    private void OnHistoryCollectionViewHandlerChanged(object? sender, EventArgs e) =>
        ConfigureAndroidHistoryScrolling();

    private void ConfigureAndroidHistoryScrolling()
    {
        if (_androidHistoryRecyclerView is not null)
            _androidHistoryRecyclerView.Touch -= OnAndroidHistoryTouch;

        _androidHistoryRecyclerView = HistoryCollectionView.Handler?.PlatformView
            as AndroidX.RecyclerView.Widget.RecyclerView;
        if (_androidHistoryRecyclerView is null)
            return;

        _androidHistoryRecyclerView.NestedScrollingEnabled = true;
        _androidHistoryRecyclerView.Touch += OnAndroidHistoryTouch;
    }

    private static void OnAndroidHistoryTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        if (sender is not Android.Views.View historyView || e.Event is null)
            return;

        var releaseParent = e.Event.ActionMasked is
            Android.Views.MotionEventActions.Up or
            Android.Views.MotionEventActions.Cancel;
        historyView.Parent?.RequestDisallowInterceptTouchEvent(!releaseParent);
        e.Handled = false;
    }

    private void ConfigureAndroidImageGestures()
    {
        if (_androidImageView is not null)
            _androidImageView.Touch -= OnAndroidImageTouch;

        _androidImageView = SensorImage.Handler?.PlatformView as Android.Widget.ImageView;
        if (_androidImageView is not null)
        {
            _androidImageView.Touch += OnAndroidImageTouch;
            ApplyAndroidDisplayedBitmap(_androidImageView);
        }
    }

    private void ConfigureAndroidFullScreenViewport()
    {
        if (_androidFullScreenViewportView is not null)
            _androidFullScreenViewportView.Touch -= OnAndroidFullScreenViewportTouch;

        _androidFullScreenViewportView =
            FullScreenImageViewport.Handler?.PlatformView as Android.Views.View;
        if (_androidFullScreenViewportView is null)
            return;

        _androidFullScreenViewportView.Clickable = true;
        _androidFullScreenViewportView.Touch += OnAndroidFullScreenViewportTouch;
        _androidFullScreenImageView =
            FullScreenSensorImage.Handler?.PlatformView as Android.Widget.ImageView;
        ApplyAndroidDisplayedBitmap(_androidFullScreenImageView);
    }

    private static void ConfigureAndroidPixelRendering(Android.Widget.ImageView? imageView)
    {
        imageView?.Drawable?.SetFilterBitmap(false);
    }

    private void OnAndroidFullScreenViewportTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        if (sender is not Android.Views.View viewport || e.Event is null)
            return;

        var action = e.Event.ActionMasked;
        var density = Microsoft.Maui.Devices.DeviceDisplay.MainDisplayInfo.Density;
        switch (action)
        {
            case Android.Views.MotionEventActions.Down:
                _androidLastPanX = e.Event.GetX();
                _androidLastPanY = e.Event.GetY();
                _androidIsPinching = false;
                viewport.Parent?.RequestDisallowInterceptTouchEvent(true);
                break;

            case Android.Views.MotionEventActions.PointerDown
                when e.Event.PointerCount >= 2:
                BeginAndroidFullScreenPinch(e.Event, density);
                break;

            case Android.Views.MotionEventActions.Move
                when e.Event.PointerCount >= 2:
                if (!_androidIsPinching)
                    BeginAndroidFullScreenPinch(e.Event, density);
                ApplyAndroidFullScreenPinch(e.Event, density);
                break;

            case Android.Views.MotionEventActions.Move
                when e.Event.PointerCount == 1 && !_androidIsPinching &&
                     FullScreenSensorImage.Scale > MinimumImageScale:
                var currentX = e.Event.GetX();
                var currentY = e.Event.GetY();
                FullScreenSensorImage.TranslationX += (currentX - _androidLastPanX) / density;
                FullScreenSensorImage.TranslationY += (currentY - _androidLastPanY) / density;
                _androidLastPanX = currentX;
                _androidLastPanY = currentY;
                ClampFullScreenImageTranslation();
                break;

            case Android.Views.MotionEventActions.Up:
            case Android.Views.MotionEventActions.Cancel:
                _androidIsPinching = false;
                ClampFullScreenImageTranslation();
                viewport.Parent?.RequestDisallowInterceptTouchEvent(false);
                break;

            case Android.Views.MotionEventActions.PointerUp:
                _androidIsPinching = false;
                if (e.Event.PointerCount > 1)
                {
                    var remainingIndex = e.Event.ActionIndex == 0 ? 1 : 0;
                    _androidLastPanX = e.Event.GetX(remainingIndex);
                    _androidLastPanY = e.Event.GetY(remainingIndex);
                }
                break;
        }

        e.Handled = true;
    }

    private void BeginAndroidFullScreenPinch(Android.Views.MotionEvent motionEvent, double density)
    {
        if (motionEvent.PointerCount < 2 || density <= 0)
            return;

        var firstX = motionEvent.GetX(0) / density;
        var firstY = motionEvent.GetY(0) / density;
        var secondX = motionEvent.GetX(1) / density;
        var secondY = motionEvent.GetY(1) / density;
        var midpointX = (firstX + secondX) / 2.0;
        var midpointY = (firstY + secondY) / 2.0;
        var deltaX = secondX - firstX;
        var deltaY = secondY - firstY;
        _androidPinchStartDistance = (float)Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (_androidPinchStartDistance < 1)
            return;

        _androidPinchStartScale = Math.Max(MinimumImageScale, FullScreenSensorImage.Scale);
        var originX = FullScreenSensorImage.Width / 2.0;
        var originY = FullScreenSensorImage.Height / 2.0;
        _androidPinchAnchorX =
            (midpointX - originX - FullScreenSensorImage.TranslationX) /
            _androidPinchStartScale + originX;
        _androidPinchAnchorY =
            (midpointY - originY - FullScreenSensorImage.TranslationY) /
            _androidPinchStartScale + originY;
        _androidIsPinching = true;
    }

    private void ApplyAndroidFullScreenPinch(Android.Views.MotionEvent motionEvent, double density)
    {
        if (!_androidIsPinching || motionEvent.PointerCount < 2 ||
            _androidPinchStartDistance < 1 || density <= 0)
            return;

        var firstX = motionEvent.GetX(0) / density;
        var firstY = motionEvent.GetY(0) / density;
        var secondX = motionEvent.GetX(1) / density;
        var secondY = motionEvent.GetY(1) / density;
        var midpointX = (firstX + secondX) / 2.0;
        var midpointY = (firstY + secondY) / 2.0;
        var deltaX = secondX - firstX;
        var deltaY = secondY - firstY;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        var scale = _androidPinchStartScale * distance / _androidPinchStartDistance;
        var originX = FullScreenSensorImage.Width / 2.0;
        var originY = FullScreenSensorImage.Height / 2.0;
        var translationX = midpointX - originX - (_androidPinchAnchorX - originX) * scale;
        var translationY = midpointY - originY - (_androidPinchAnchorY - originY) * scale;
        ApplyFullScreenImageTransform(scale, translationX, translationY);
    }

    private void OnAndroidImageTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        if (sender is not Android.Views.View imageView || e.Event is null)
            return;

        var action = e.Event.ActionMasked;
        var imageOwnsGesture = e.Event.PointerCount > 1 || SensorImage.Scale > MinimumImageScale;
        if (action is Android.Views.MotionEventActions.PointerDown ||
            action is Android.Views.MotionEventActions.Move && imageOwnsGesture ||
            action is Android.Views.MotionEventActions.Down && SensorImage.Scale > MinimumImageScale)
        {
            imageView.Parent?.RequestDisallowInterceptTouchEvent(true);
        }
        else if (action is Android.Views.MotionEventActions.Up or Android.Views.MotionEventActions.Cancel)
        {
            imageView.Parent?.RequestDisallowInterceptTouchEvent(false);
        }

        // MAUI Pinch/Pan recognizer'larının aynı olayı işlemesine izin ver.
        e.Handled = false;
    }
#endif

    protected override bool OnBackButtonPressed()
    {
        if (AppDialogOverlay.IsVisible)
        {
            AppDialogOverlay.IsVisible = false;
            return true;
        }

        if (FullScreenViewerOverlay.IsVisible)
        {
            CloseFullScreenViewer();
            return true;
        }

        if (DeviceControlPage.IsVisible)
        {
            _ = CloseDeviceControlAsync();
            return true;
        }

        return base.OnBackButtonPressed();
    }

#if WINDOWS
    private void OnPageScrollViewHandlerChanged(object? sender, EventArgs e) =>
        ConfigureWindowsScrollViewer();

    private void ConfigureWindowsScrollViewer()
    {
        if (PageScrollView.Handler?.PlatformView is not
            Microsoft.UI.Xaml.Controls.ScrollViewer scrollViewer)
            return;

        scrollViewer.VerticalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Enabled;
        scrollViewer.VerticalScrollBarVisibility =
            Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Visible;
        scrollViewer.IsVerticalRailEnabled = true;
        scrollViewer.IsVerticalScrollChainingEnabled = false;
        scrollViewer.IsScrollInertiaEnabled = false;
        scrollViewer.HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
        scrollViewer.HorizontalScrollBarVisibility =
            Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
        scrollViewer.IsHorizontalRailEnabled = false;
    }
#endif

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    public Task HandleWindowStoppedAsync() => _viewModel.HandleWindowStoppedAsync();

    public Task HandleWindowDestroyingAsync()
    {
#if ANDROID
        _viewModel.PresentationFrameReady -= OnAndroidPresentationFrameReady;
        HistoryCollectionView.HandlerChanged -= OnHistoryCollectionViewHandlerChanged;
        if (_androidHistoryRecyclerView is not null)
        {
            _androidHistoryRecyclerView.Touch -= OnAndroidHistoryTouch;
            _androidHistoryRecyclerView = null;
        }
        lock (_androidPresentationGate)
        {
            _androidPresentationStopping = true;
            _androidPendingPresentationFrame = null;
        }
#endif
        return _viewModel.HandleWindowDestroyingAsync();
    }
}
