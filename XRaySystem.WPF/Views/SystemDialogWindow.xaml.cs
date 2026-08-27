using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace XRaySystem.WPF.Views;

public enum SystemDialogSeverity
{
    Information,
    Warning,
    Error
}

public partial class SystemDialogWindow : Window
{
    public string DialogHeading { get; }
    public string DialogMessage { get; }
    public string SeverityText { get; }
    public Brush AccentBrush { get; }

    private SystemDialogWindow(
        string message,
        string heading,
        SystemDialogSeverity severity)
    {
        DialogMessage = string.IsNullOrWhiteSpace(message)
            ? "No additional information is available."
            : message.Trim();
        DialogHeading = string.IsNullOrWhiteSpace(heading) ? "System Message" : heading.Trim();
        (SeverityText, AccentBrush) = severity switch
        {
            SystemDialogSeverity.Error =>
                ("ACTION REQUIRED", new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E))),
            SystemDialogSeverity.Warning =>
                ("ATTENTION", new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20))),
            _ => ("INFORMATION", new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)))
        };
        AccentBrush.Freeze();

        InitializeComponent();
        DataContext = this;
    }

    public static void ShowMessage(
        string message,
        string heading,
        SystemDialogSeverity severity = SystemDialogSeverity.Information)
    {
        var application = System.Windows.Application.Current;
        if (application?.Dispatcher is null)
            throw new InvalidOperationException("The WPF application dispatcher is unavailable.");

        void ShowCore()
        {
            var dialog = new SystemDialogWindow(message, heading, severity);
            var owner = application.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive && window.IsVisible) ??
                application.MainWindow;
            if (owner is { IsLoaded: true, IsVisible: true })
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.ShowDialog();
        }

        if (application.Dispatcher.CheckAccess()) ShowCore();
        else application.Dispatcher.Invoke(ShowCore);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed &&
            e.GetPosition(this).Y <= 82 &&
            e.OriginalSource is not System.Windows.Controls.Button)
            DragMove();
    }
}
