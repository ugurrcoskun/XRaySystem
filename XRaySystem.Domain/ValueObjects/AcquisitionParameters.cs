namespace XRaySystem.Domain.ValueObjects;

/// <summary>
/// Kullanıcının istediği acquisition değerleri. Gerçek sensörün ölçtüğü değerler
/// SensorReadings içinde ayrı tutulur. Watt -> mA dönüşümü sensör adapter'ının işidir.
/// </summary>
public sealed record AcquisitionParameters(
    double VoltageKv,
    double PowerW,
    double ExposureTimeMs)
{
    public double TubeCurrentMa => PowerW / VoltageKv;

    public bool IsValid =>
        double.IsFinite(VoltageKv) && VoltageKv > 0 &&
        double.IsFinite(PowerW) && PowerW > 0 &&
        double.IsFinite(ExposureTimeMs) && ExposureTimeMs is >= 1 and <= 3000;

    public void EnsureValid()
    {
        if (!IsValid)
            throw new ArgumentOutOfRangeException(
                nameof(AcquisitionParameters),
                "kV ve Watt sıfırdan büyük, exposure süresi 1-3000 ms aralığında olmalıdır.");
    }
}
