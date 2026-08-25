using System.Globalization;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Tests;

public sealed class AcquisitionFileNameFactoryTests
{
    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void Filename_is_invariant_and_uses_utc_with_required_fields(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var stem = AcquisitionFileNameFactory.CreateStem(
                70.25,
                2.5,
                1000.125,
                new DateTimeOffset(2026, 8, 24, 15, 30, 45, 678, TimeSpan.FromHours(3)));

            Assert.Equal("image_70.3kV_2.50mA_1000.125ms_20260824_123045_678", stem);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(0, 2, 10)]
    [InlineData(70, 0, 10)]
    [InlineData(70, 2, double.NaN)]
    public void Filename_rejects_non_positive_or_non_finite_values(
        double voltage,
        double current,
        double exposure) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AcquisitionFileNameFactory.CreateStem(voltage, current, exposure, DateTimeOffset.UtcNow));
}
