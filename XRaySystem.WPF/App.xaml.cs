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
using XRaySystem.WPF.Views;
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
            // Surface unexpected UI-thread failures without silently terminating the operator session.
            this.DispatcherUnhandledException += (s, e) =>
            {
                ShowCriticalError(
                    $"RUNTIME ERROR:\n{e.Exception.Message}\n\nDetails:\n{e.Exception.InnerException?.Message}",
                    "Critical Error");
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

                // Start database preparation in parallel so the shell can render immediately.
                // History loading and SNAP persistence explicitly await this task.
                DatabaseReady = Task.Run(InitializeDatabase);

                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();
            }
            catch (System.Exception ex)
            {
                ShowCriticalError(
                    $"STARTUP ERROR:\n{ex.Message}\n\nDetails:\n{ex.InnerException?.Message}",
                    "Unable to Start");
            }
        }

        private static void ShowCriticalError(string message, string title)
        {
            try
            {
                SystemDialogWindow.ShowMessage(message, title, SystemDialogSeverity.Error);
            }
            catch
            {
                // The native fallback is retained only for failures that prevent
                // the application's own XAML dialog from being constructed.
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
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
                throw;
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
