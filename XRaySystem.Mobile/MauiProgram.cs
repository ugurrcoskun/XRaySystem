using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.Storage;
using System.Net.Http;
using XRaySystem.Application.UseCases;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Infrastructure.Persistence;
using XRaySystem.Infrastructure.Sensors;
using XRaySystem.Mobile.Services;
using XRaySystem.Mobile.ViewModels;
using XRaySystem.RemoteClient;

namespace XRaySystem.Mobile
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            // Mobil geçmiş WPF'nin kalıcı TIFF veritabanından bağımsız tutulur.
            var mobileDatabasePath = Path.Combine(FileSystem.AppDataDirectory, "XRaySystem.Mobile.db");
            builder.Services.AddDbContext<XRayDbContext>(options =>
                options.UseSqlite($"Data Source={mobileDatabasePath}"));
            builder.Services.AddScoped<IXRayMeasurementRepository, XRayMeasurementRepository>();

            // Local varsayılandır. Android Emulator remote modda Windows hostuna 10.0.2.2 ile ulaşır.
            var defaultDeviceHost = OperatingSystem.IsAndroid()
                ? new Uri("http://10.0.2.2:5088")
                : new Uri("http://127.0.0.1:5088");
            var modeSettings = new MobileSensorModeSettings();
            var runtime = modeSettings.Load(defaultDeviceHost);
            builder.Services.AddSingleton(modeSettings);
            builder.Services.AddSingleton(runtime);
            if (runtime.Mode == SensorRuntimeMode.Remote)
            {
                builder.Services.AddSingleton(new RemoteDeviceClientOptions(
                    runtime.DeviceHostBaseAddress,
                    runtime.DeviceId,
                    ApiKey: Environment.GetEnvironmentVariable("XRAYSYSTEM_DEVICEHOST_API_KEY")));
                builder.Services.AddSingleton(new HttpClient());
                builder.Services.AddSingleton<RemoteDeviceClient>();
                builder.Services.AddSingleton<IRemoteDeviceClient>(provider =>
                    provider.GetRequiredService<RemoteDeviceClient>());
                builder.Services.AddSingleton<ISensorService, RemoteSensorService>();
            }
            else
            {
                builder.Services.AddSingleton<ISensorService, MockXRaySensorService>();
            }

            // Services
            builder.Services.AddSingleton<MauiImageStorageService>();
            builder.Services.AddSingleton<MauiWindowLevelRenderer>();

            // Application Use Cases
            builder.Services.AddTransient<SaveMeasurementUseCase>();
            builder.Services.AddTransient<SnapMeasurementUseCase>();
            builder.Services.AddTransient<ProcessTelemetryUseCase>();
            builder.Services.AddTransient<GetMeasurementHistoryUseCase>();

            // ViewModels
            builder.Services.AddTransient<MainViewModel>();

            // Views
            builder.Services.AddTransient<MainPage>();

            var app = builder.Build();

            // Migration'ları uygulama oluşturulmadan uygula; eski veritabanları veri kaybetmeden baseline edilir.
            using (var scope = app.Services.CreateScope())
            {
                try
                {
                    var db = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
                    LegacyDatabaseBaseliner.Prepare(db);
                    db.Database.Migrate();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DB Init Error: {ex.Message}");
                }
            }

            return app;
        }
    }
}
