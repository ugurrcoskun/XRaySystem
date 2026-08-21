using Microsoft.Maui.Controls;
using XRaySystem.Mobile.ViewModels;

namespace XRaySystem.Mobile;

public partial class MainPage : ContentPage
{
    private const double MinimumImageScale = 1.0;
    // Kullanıcıya görünür üst limit yoktur; yalnız platform transform taşmasını engeller.
    private const double NumericSafetyImageScale = 1_000_000.0;
    private const double ZoomFactor = 1.25;
    private const double CoordinateFocusScale = 8.0;

    private readonly MainViewModel _viewModel;
    private bool? _isWideLayout;
    private double _pinchStartScale = MinimumImageScale;
    private double _panStartX;
    private double _panStartY;
    private double _fullScreenPinchStartScale = MinimumImageScale;
    private double _fullScreenPanStartX;
    private double _fullScreenPanStartY;
#if ANDROID
    private Android.Widget.ImageView? _androidImageView;
    private Android.Widget.ImageView? _androidFullScreenImageView;
    private Android.Views.ScaleGestureDetector? _androidFullScreenScaleDetector;
    private float _androidLastPanX;
    private float _androidLastPanY;
#endif

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        SensorImage.GestureRecognizers.Remove(ImagePanGesture);
        _viewModel = viewModel;
        BindingContext = viewModel;
        _viewModel.ReviewImageOpened += OnReviewImageOpened;
#if WINDOWS
        PageScrollView.HandlerChanged += OnPageScrollViewHandlerChanged;
        ConfigureWindowsScrollViewer();
#endif
#if ANDROID
        FullScreenSensorImage.GestureRecognizers.Remove(FullScreenPinchGesture);
        FullScreenSensorImage.GestureRecognizers.Remove(FullScreenPanGesture);
        SensorImage.HandlerChanged += OnSensorImageHandlerChanged;
        FullScreenSensorImage.HandlerChanged += OnFullScreenSensorImageHandlerChanged;
        SensorImage.PropertyChanged += OnAndroidImagePropertyChanged;
        FullScreenSensorImage.PropertyChanged += OnAndroidImagePropertyChanged;
        ConfigureAndroidImageGestures();
        ConfigureAndroidFullScreenImage();
#endif
    }

    private void OnOpenFullScreenTapped(object? sender, TappedEventArgs e)
        => OpenFullScreenViewer();

    private void OnOpenFullScreenClicked(object? sender, EventArgs e)
        => OpenFullScreenViewer();

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
        Dispatcher.Dispatch(UpdateFullScreenCoordinateReadout);
#if ANDROID
        ConfigureAndroidPixelRendering(_androidFullScreenImageView);
#endif
    }

    private void OnCloseFullScreenClicked(object? sender, EventArgs e) => CloseFullScreenViewer();

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

    private void OnFullScreenImageSizeChanged(object? sender, EventArgs e) =>
        UpdateFullScreenCoordinateReadout();

    private async void OnReviewImageOpened()
    {
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
        if (focusPoint is Point focus &&
            FullScreenSensorImage.Width > 0 && FullScreenSensorImage.Height > 0 &&
            Math.Abs(newScale - oldScale) > double.Epsilon)
        {
            var originX = FullScreenSensorImage.Width / 2.0;
            var originY = FullScreenSensorImage.Height / 2.0;
            var localX = (focus.X - originX - FullScreenSensorImage.TranslationX) / oldScale + originX;
            var localY = (focus.Y - originY - FullScreenSensorImage.TranslationY) / oldScale + originY;
            FullScreenSensorImage.TranslationX =
                focus.X - originX - (localX - originX) * newScale;
            FullScreenSensorImage.TranslationY =
                focus.Y - originY - (localY - originY) * newScale;
        }

        FullScreenSensorImage.Scale = newScale;
        if (FullScreenSensorImage.Scale <= MinimumImageScale)
        {
            FullScreenSensorImage.TranslationX = 0;
            FullScreenSensorImage.TranslationY = 0;
        }
        else
        {
            ClampFullScreenImageTranslation();
        }

        FullScreenZoomLevelLabel.Text = $"{Math.Round(FullScreenSensorImage.Scale * 100):F0}%";
        UpdateFullScreenCoordinateReadout();
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
        FullScreenSensorImage.Scale = MinimumImageScale;
        FullScreenSensorImage.TranslationX = 0;
        FullScreenSensorImage.TranslationY = 0;
        FullScreenZoomLevelLabel.Text = "100%";
        UpdateFullScreenCoordinateReadout();
    }

    private void ClampImageTranslation()
    {
        var maximumX = Math.Max(0, SensorImage.Width * (SensorImage.Scale - 1) / 2);
        var maximumY = Math.Max(0, SensorImage.Height * (SensorImage.Scale - 1) / 2);
        SensorImage.TranslationX = Math.Clamp(SensorImage.TranslationX, -maximumX, maximumX);
        SensorImage.TranslationY = Math.Clamp(SensorImage.TranslationY, -maximumY, maximumY);
    }

    private void ClampFullScreenImageTranslation()
    {
        var maximumX = Math.Max(0, FullScreenSensorImage.Width * (FullScreenSensorImage.Scale - 1) / 2);
        var maximumY = Math.Max(0, FullScreenSensorImage.Height * (FullScreenSensorImage.Scale - 1) / 2);
        FullScreenSensorImage.TranslationX =
            Math.Clamp(FullScreenSensorImage.TranslationX, -maximumX, maximumX);
        FullScreenSensorImage.TranslationY =
            Math.Clamp(FullScreenSensorImage.TranslationY, -maximumY, maximumY);
        UpdateFullScreenCoordinateReadout();
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
                ? $"GEÇERSİZ · X: 0–{width - 1}  Y: 0–{height - 1}"
                : "GÖRÜNTÜ BOYUTU BULUNAMADI";
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
                ? $"MERKEZ X: {x}  Y: {y}"
                : "MERKEZ X: —  Y: —";
    }

#if ANDROID
    private void OnSensorImageHandlerChanged(object? sender, EventArgs e) =>
        ConfigureAndroidImageGestures();

    private void OnFullScreenSensorImageHandlerChanged(object? sender, EventArgs e) =>
        ConfigureAndroidFullScreenImage();

    private void OnAndroidImagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != Image.SourceProperty.PropertyName)
            return;

        if (ReferenceEquals(sender, SensorImage))
            ConfigureAndroidPixelRendering(_androidImageView);
        else if (ReferenceEquals(sender, FullScreenSensorImage))
            ConfigureAndroidPixelRendering(_androidFullScreenImageView);
    }

    private void ConfigureAndroidImageGestures()
    {
        if (_androidImageView is not null)
            _androidImageView.Touch -= OnAndroidImageTouch;

        _androidImageView = SensorImage.Handler?.PlatformView as Android.Widget.ImageView;
        if (_androidImageView is not null)
        {
            _androidImageView.Touch += OnAndroidImageTouch;
            ConfigureAndroidPixelRendering(_androidImageView);
        }
    }

    private void ConfigureAndroidFullScreenImage()
    {
        if (_androidFullScreenImageView is not null)
            _androidFullScreenImageView.Touch -= OnAndroidFullScreenImageTouch;

        _androidFullScreenImageView =
            FullScreenSensorImage.Handler?.PlatformView as Android.Widget.ImageView;
        if (_androidFullScreenImageView is null)
            return;

        _androidFullScreenScaleDetector = new Android.Views.ScaleGestureDetector(
            _androidFullScreenImageView.Context,
            new FullScreenScaleListener(this));
        _androidFullScreenImageView.Touch += OnAndroidFullScreenImageTouch;
        ConfigureAndroidPixelRendering(_androidFullScreenImageView);
    }

    private static void ConfigureAndroidPixelRendering(Android.Widget.ImageView? imageView)
    {
        imageView?.Drawable?.SetFilterBitmap(false);
    }

    private void OnAndroidFullScreenImageTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        if (sender is not Android.Views.View imageView || e.Event is null)
            return;

        _androidFullScreenScaleDetector?.OnTouchEvent(e.Event);
        var action = e.Event.ActionMasked;
        var density = Microsoft.Maui.Devices.DeviceDisplay.MainDisplayInfo.Density;
        switch (action)
        {
            case Android.Views.MotionEventActions.Down:
                _androidLastPanX = e.Event.GetX();
                _androidLastPanY = e.Event.GetY();
                imageView.Parent?.RequestDisallowInterceptTouchEvent(true);
                break;

            case Android.Views.MotionEventActions.Move
                when e.Event.PointerCount == 1 &&
                     _androidFullScreenScaleDetector?.IsInProgress != true &&
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
                imageView.Parent?.RequestDisallowInterceptTouchEvent(false);
                break;

            case Android.Views.MotionEventActions.PointerUp:
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

    private void ApplyAndroidFullScreenScale(Android.Views.ScaleGestureDetector detector)
    {
        var density = Microsoft.Maui.Devices.DeviceDisplay.MainDisplayInfo.Density;
        SetFullScreenImageScale(
            FullScreenSensorImage.Scale * detector.ScaleFactor,
            new Point(detector.FocusX / density, detector.FocusY / density));
    }

    private sealed class FullScreenScaleListener(MainPage owner)
        : Android.Views.ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        public override bool OnScaleBegin(Android.Views.ScaleGestureDetector detector) => true;

        public override bool OnScale(Android.Views.ScaleGestureDetector detector)
        {
            owner.ApplyAndroidFullScreenScale(detector);
            return true;
        }
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
        if (FullScreenViewerOverlay.IsVisible)
        {
            CloseFullScreenViewer();
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

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0) return;

        var useWideLayout = width >= 900;
        if (_isWideLayout == useWideLayout) return;
        _isWideLayout = useWideLayout;

        Workspace.ColumnDefinitions.Clear();
        if (useWideLayout)
        {
            Workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            Workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            Grid.SetRow(ControlPanel, 0);
            Grid.SetColumn(ControlPanel, 0);
            Grid.SetRow(ViewerPanel, 0);
            Grid.SetColumn(ViewerPanel, 1);
        }
        else
        {
            Workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            Grid.SetRow(ViewerPanel, 0);
            Grid.SetColumn(ViewerPanel, 0);
            Grid.SetRow(ControlPanel, 1);
            Grid.SetColumn(ControlPanel, 0);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    public Task HandleWindowStoppedAsync() => _viewModel.HandleWindowStoppedAsync();

    public Task HandleWindowDestroyingAsync() => _viewModel.HandleWindowDestroyingAsync();
}
