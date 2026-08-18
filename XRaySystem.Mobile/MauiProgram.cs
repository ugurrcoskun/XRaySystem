using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using XRaySystem.Application.UseCases;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Infrastructure.Persistence;
using XRaySystem.Infrastructure.Sensors;
using XRaySystem.Mobile.Services;
using XRaySystem.Mobile.ViewModels;

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

            // Database
            builder.Services.AddDbContext<XRayDbContext>();
            builder.Services.AddScoped<IXRayMeasurementRepository, XRayMeasurementRepository>();

            // Mock Sensor
            builder.Services.AddSingleton<ISensorService, MockXRaySensorService>();

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
