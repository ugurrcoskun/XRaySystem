using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using XRaySystem.Application.UseCases;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Infrastructure.Cloud;
using XRaySystem.Infrastructure.Persistence;
using XRaySystem.Infrastructure.Sensors;
using System.Runtime.InteropServices;
using XRaySystem.WPF.Services;
using XRaySystem.WPF.ViewModels;

namespace XRaySystem.WPF
{
    public partial class App : System.Windows.Application
    {
        public static ServiceProvider ServiceProvider { get; private set; }
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

                // Veritabanını senkron olarak oluştur (Açılışta tabloların 100% hazır olmasını sağla)
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
                db.Database.EnsureCreated();
                UpgradeSchema(db);
            }
            catch (Exception ex)
            {
                DatabaseInitializationFailure = ex;
                MessageBox.Show($"Database initialization failed:\n{ex.Message}", "Database", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void UpgradeSchema(XRayDbContext db)
        {
            var connection = db.Database.GetDbConnection();
            connection.Open();
            using var info = connection.CreateCommand();
            info.CommandText = "PRAGMA table_info('Measurements');";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = info.ExecuteReader())
                while (reader.Read()) columns.Add(reader.GetString(1));

            foreach (var (name, definition) in new[] {
                ("ImagePath", "TEXT"), ("Profile", "INTEGER NOT NULL DEFAULT 0"),
                ("IsSimulator", "INTEGER NOT NULL DEFAULT 0") })
            {
                if (columns.Contains(name)) continue;
                using var command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE Measurements ADD COLUMN {name} {definition};";
                try { command.ExecuteNonQuery(); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                    ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
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

            // ── Sensör Modu Seçimi ────────────────────────────────────────────────
            // Matematiksel 16-bit Dijital Test Fantomu simülatörü (MockXRaySensorService)
            services.AddSingleton<ISensorService, MockXRaySensorService>();

            services.AddSingleton<ICloudPublisher, NullCloudPublisher>();

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
