using System.Windows;
using Microsoft.EntityFrameworkCore;
using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using XRaySystem.Application.UseCases;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Infrastructure.Persistence;
using XRaySystem.Infrastructure.Sensors;
using System.Runtime.InteropServices;
using XRaySystem.WPF.Services;
using XRaySystem.WPF.ViewModels;
using XRaySystem.RemoteClient;

namespace XRaySystem.WPF
{
    public partial class App : System.Windows.Application
    {
        public static ServiceProvider ServiceProvider { get; private set; } = null!;
        public static Task DatabaseReady { get; private set; } = Task.CompletedTask;
        public static Exception? DatabaseInitializationFailure { get; private set; }

        public App()
        {
            // Beklenmeyen çökmeleri ekrana MessageBox olarak yansıt
            this.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show($"ÇALIŞMA ZAMANI HATASI:\n{e.Exception.Message}\n\nDetay:\n{e.Exception.InnerException?.Message}", "Kritik Çökme", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);

                var serviceCollection = new ServiceCollection();
                ConfigureServices(serviceCollection);

                ServiceProvider = serviceCollection.BuildServiceProvider();

                // Migration'ları pencere açılmadan uygula; eski veritabanları veri kaybetmeden baseline edilir.
                InitializeDatabase();

                // Dependency Injection üzerinden ana pencereyi ayağa kaldır
                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"BAŞLANGIÇ HATASI:\n{ex.Message}\n\nDetay:\n{ex.InnerException?.Message}", "Başlatılamadı", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void InitializeDatabase()
        {
            try
            {
                using var scope = ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
                LegacyDatabaseBaseliner.Prepare(db);
                db.Database.Migrate();
            }
            catch (Exception ex)
            {
                DatabaseInitializationFailure = ex;
                MessageBox.Show($"Database initialization failed:\n{ex.Message}", "Database", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // DbContext yaşam döngüsü işlemlerle sınırlı olmalı; repository her işlem için
            // kendi scope'u içinden yeni bir context alır.
            services.AddDbContext<XRaySystem.Infrastructure.Persistence.XRayDbContext>();
            services.AddScoped<IXRayMeasurementRepository, XRaySystem.Infrastructure.Persistence.XRayMeasurementRepository>();

            // Image Storage (WPF projesinde çünkü WPF tiplerini kullanıyor)
            services.AddSingleton<IImageStorageService, ImageStorageService>();
            services.AddSingleton<WindowLevelRenderer>();

            // Local varsayılandır. Remote mod environment ayarlarıyla DeviceHost'a geçer.
            var runtime = SensorRuntimeOptions.FromEnvironment(new Uri("http://127.0.0.1:5088"));
            services.AddSingleton(runtime);
            if (runtime.Mode == SensorRuntimeMode.Remote)
            {
                services.AddSingleton(new RemoteDeviceClientOptions(
                    runtime.DeviceHostBaseAddress,
                    runtime.DeviceId,
                    ApiKey: Environment.GetEnvironmentVariable("XRAYSYSTEM_DEVICEHOST_API_KEY")));
                services.AddSingleton(new HttpClient());
                services.AddSingleton<RemoteDeviceClient>();
                services.AddSingleton<IRemoteDeviceClient>(provider =>
                    provider.GetRequiredService<RemoteDeviceClient>());
                services.AddSingleton<ISensorService, RemoteSensorService>();
            }
            else
            {
                services.AddSingleton<ISensorService, MockXRaySensorService>();
            }

            // Application (Use Cases)
            services.AddTransient<StartCaptureUseCase>();
            services.AddTransient<ProcessTelemetryUseCase>();
            services.AddTransient<SaveMeasurementUseCase>();
            services.AddTransient<GetMeasurementHistoryUseCase>();
            services.AddTransient<SnapMeasurementUseCase>();
            services.AddTransient<ResumeLiveUseCase>();

            // ViewModels
            services.AddTransient<LiveTelemetryViewModel>();

            // Views
            services.AddTransient<MainWindow>();
        }
    }
}
