using Microsoft.Maui.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace XRaySystem.Mobile
{
    public partial class App : Microsoft.Maui.Controls.Application
    {
        private readonly IServiceProvider _services;

        public App(IServiceProvider services)
        {
            InitializeComponent();
            _services = services;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // App kaynakları InitializeComponent ile yüklendikten sonra sayfayı oluştur.
            // Böylece MainPage içindeki StaticResource anahtarları çözülebilir.
            var page = _services.GetRequiredService<MainPage>();
            var window = new Window(page);
            window.Stopped += async (_, _) => await page.HandleWindowStoppedAsync();
            window.Destroying += async (_, _) => await page.HandleWindowDestroyingAsync();
            return window;
        }
    }
}
