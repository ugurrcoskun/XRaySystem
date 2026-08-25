using System.Globalization;
using XRaySystem.Domain.Entities;

namespace XRaySystem.Domain.ValueObjects;

public static class AcquisitionFileNameFactory
{
    public static string CreateStem(XRayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        return CreateStem(
            measurement.Readings.Voltage,
            measurement.Readings.TubeCurrent,
            measurement.Readings.ExposureTime,
            new DateTimeOffset(DateTime.SpecifyKind(measurement.Timestamp, DateTimeKind.Utc)));
    }

    public static string CreateStem(
        double voltageKv,
        double tubeCurrentMa,
        double exposureTimeMs,
        DateTimeOffset capturedAt)
    {
        EnsurePositiveFinite(voltageKv, nameof(voltageKv));
        EnsurePositiveFinite(tubeCurrentMa, nameof(tubeCurrentMa));
        EnsurePositiveFinite(exposureTimeMs, nameof(exposureTimeMs));
        var utc = capturedAt.ToUniversalTime();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"image_{voltageKv:0.0}kV_{tubeCurrentMa:0.00}mA_{exposureTimeMs:0.###}ms_{utc:yyyyMMdd_HHmmss_fff}");
    }

    private static void EnsurePositiveFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name);
    }
}
