using System;
using System.Threading;
using System.Threading.Tasks;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Sensors;

public class MockXRaySensorService : ISensorService, IDisposable
{
    private DeviceStatus _currentStatus = DeviceStatus.Disconnected;
    private CancellationTokenSource? _captureTokenSource;
    private readonly Random _random = new();

    // Simülasyon çözünürlüğü
    public const int Width = 2324;
    public const int Height = 2930;

    // Canlı önizleme çözünürlüğü – UI thread'i boğmaması için küçük tutuyoruz
    public const int LiveWidth  = 512;
    public const int LiveHeight = 512;

    public DeviceStatus CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (_currentStatus != value)
            {
                _currentStatus = value;
                StatusChanged?.Invoke(_currentStatus);
            }
        }
    }

    public event Action<DeviceStatus>? StatusChanged;
    public event Action<XRayMeasurement>? MeasurementReceived;

    public async Task ConnectAsync()
    {
        // Donanıma bağlanıyormuş gibi kısa bir gecikme
        await Task.Delay(500); 
        CurrentStatus = DeviceStatus.Connected;
    }

    public async Task DisconnectAsync()
    {
        await StopCaptureAsync();
        CurrentStatus = DeviceStatus.Disconnected;
    }

    public Task StartCaptureAsync()
    {
        if (CurrentStatus != DeviceStatus.Connected)
        {
            throw new InvalidOperationException("Cihaz bağlı değil. Önce ConnectAsync çağrılmalıdır.");
        }

        CurrentStatus = DeviceStatus.Capturing;
        _captureTokenSource = new CancellationTokenSource();
        
        // Arka planda döngüyü başlat (Fire and Forget)
        _ = GenerateMockDataLoopAsync(_captureTokenSource.Token);
        
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync()
    {
        _captureTokenSource?.Cancel();
        if (CurrentStatus == DeviceStatus.Capturing)
        {
            CurrentStatus = DeviceStatus.Connected;
        }
        return Task.CompletedTask;
    }

    public async Task<XRayMeasurement> SnapAsync(double exposureTime)
    {
        if (CurrentStatus == DeviceStatus.Disconnected)
        {
            throw new InvalidOperationException("Cihaz bağlı değil.");
        }

        _isPaused = true;

        // Snap işlemi (donanımın exposure süresi kadar beklemesi simülasyonu)
        await Task.Delay((int)exposureTime);

        var readings = new SensorReadings(
            TubeCurrent: Math.Round(50.0 + _random.NextDouble() * 5, 2), // Snap'te daha yüksek akım
            Voltage: Math.Round(70.0 + _random.NextDouble() * 30.0, 1),  // Snap'te 70-100 kV arası
            XRayIntensity: _random.Next(2000, 3000),                     // Yüksek yoğunluk
            ExposureTime: exposureTime
        );

        // Snap için tam çözünürlük (2324×2930), daha düşük gürültü
        ushort[] mockImage = GenerateSyntheticImage(Width, Height, noiseLevel: 500);
        
        var measurement = new XRayMeasurement(readings, mockImage);
        
        return measurement;
    }

    public Task ResumeLiveAsync()
    {
        _isPaused = false;
        return Task.CompletedTask;
    }

    private bool _isPaused = false;

    private async Task GenerateMockDataLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    await Task.Delay(100, token);
                    continue;
                }
                // Canlı yayında düşük pozlama süresi (Örn 33ms ~ 30fps)
                double liveExposure = 33.0; 

                var readings = new SensorReadings(
                    TubeCurrent: Math.Round(20.0 + _random.NextDouble() * 10, 2),
                    Voltage: Math.Round(40.0 + _random.NextDouble() * 20.0, 1), // Canlıda 40-60 kV arası
                    XRayIntensity: _random.Next(800, 1200),
                    ExposureTime: liveExposure
                );

                // Canlı yayın için düşük çözünürlüklü önizleme (512x512)
                ushort[] mockImage = GenerateSyntheticImage(LiveWidth, LiveHeight, noiseLevel: 2500);

                var measurement = new XRayMeasurement(readings, mockImage);
                
                // Arayüze fırlat
                MeasurementReceived?.Invoke(measurement);

                // 2 FPS – UI thread'in nefes alması için
                await Task.Delay(500, token);
            }
        }
        catch (TaskCanceledException)
        {
            // Yakalama durduruldu, normal bir durum.
        }
    }

    private ushort[] GenerateSyntheticImage(int imageWidth, int imageHeight, int noiseLevel)
    {
        ushort[] pixels = new ushort[imageWidth * imageHeight];

        // Ölçekleme: hangi boyutta üretilirse üretilsin aynı desen görünsün
        double scaleX = (double)imageWidth  / Width;
        double scaleY = (double)imageHeight / Height;

        Parallel.For(0, imageHeight, y =>
        {
            Random localRandom = new Random(y * 1000 + noiseLevel);

            // Gerçek koordinata çevir
            int realY = (int)(y / scaleY);

            for (int x = 0; x < imageWidth; x++)
            {
                int index  = y * imageWidth + x;
                int realX  = (int)(x / scaleX);
                ushort baseValue = 5000; // Arka plan (Hava)

                // 1. STEP WEDGE
                if (realY >= 200 && realY < 600 && realX >= 150 && realX < 2150)
                {
                    int step = (realX - 150) / 200;
                    if (step >= 0 && step < 10)
                        baseValue = (ushort)(10000 + step * 5500);
                }
                // 2. RESOLUTION LINES
                else if (realY >= 800 && realY < 1400)
                {
                    if      (realX >= 200  && realX < 600  && (realX / 16) % 2 == 0) baseValue = 50000;
                    else if (realX >= 700  && realX < 1000 && (realX / 8)  % 2 == 0) baseValue = 50000;
                    else if (realX >= 1100 && realX < 1300 && (realX / 4)  % 2 == 0) baseValue = 50000;
                    else if (realX >= 1400 && realX < 1600 && (realX / 2)  % 2 == 0) baseValue = 50000;
                    else if (realX >= 1700 && realX < 1900 &&  realX       % 2 == 0) baseValue = 50000;
                }
                // 3. CONTRAST CIRCLES
                else if (realY >= 1600 && realY < 2600)
                {
                    int dx1 = realX - 500,  dy1 = realY - 2100;
                    int dx2 = realX - 1150, dy2 = realY - 2100;
                    int dx3 = realX - 1800, dy3 = realY - 2100;

                    if      (dx1*dx1 + dy1*dy1 < 40000) baseValue = 45000;
                    else if (dx2*dx2 + dy2*dy2 < 22500) baseValue = 30000;
                    else if (dx3*dx3 + dy3*dy3 < 10000) baseValue = 15000;
                }

                // Gürültü
                int noise      = localRandom.Next(-noiseLevel, noiseLevel);
                int finalValue = baseValue + noise;
                pixels[index]  = (ushort)Math.Clamp(finalValue, 0, 65535);
            }
        });

        return pixels;
    }

    public void Dispose()
    {
        _captureTokenSource?.Cancel();
        _captureTokenSource?.Dispose();
    }
}
