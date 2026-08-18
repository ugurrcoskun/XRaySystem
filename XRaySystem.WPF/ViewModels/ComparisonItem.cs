using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using XRaySystem.Application.DTOs;

namespace XRaySystem.WPF.ViewModels;

/// <summary>
/// Karşılaştırma listesine eklenen bir görüntüyü temsil eder.
/// DTO + yüklü BitmapSource + seçim durumu içerir.
/// </summary>
public partial class ComparisonItem : ObservableObject
{
    public SensorDataDto Dto { get; }

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    public string Label => Dto.Timestamp.ToString("HH:mm:ss");
    public double Voltage => Dto.Voltage;
    public double TubeCurrent => Dto.TubeCurrent;
    public double ExposureTime => Dto.ExposureTime;
    public double PowerKW => Math.Round(Dto.Voltage * Dto.TubeCurrent / 1000.0, 2);
    public string? ImagePath => Dto.ImagePath;

    public ComparisonItem(SensorDataDto dto, BitmapSource? thumbnail = null)
    {
        Dto = dto;
        _thumbnail = thumbnail;
    }
}
