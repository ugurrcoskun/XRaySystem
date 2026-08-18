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
            return new Window(_services.GetRequiredService<MainPage>());
        }
    }
}
