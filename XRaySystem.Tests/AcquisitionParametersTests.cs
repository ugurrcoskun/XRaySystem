using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Tests;

public sealed class AcquisitionParametersTests
{
    [Fact]
    public void Valid_parameters_convert_watt_to_milliampere_inside_adapter_model()
    {
        var parameters = new AcquisitionParameters(80, 400, 250);

        Assert.True(parameters.IsValid);
        Assert.Equal(5, parameters.TubeCurrentMa);
        parameters.EnsureValid();
    }

    [Theory]
    [InlineData(0, 100, 10)]
    [InlineData(80, 0, 10)]
    [InlineData(80, 100, 0)]
    [InlineData(80, 100, 3001)]
    [InlineData(double.NaN, 100, 10)]
    public void Invalid_parameters_are_rejected(double voltage, double power, double exposure)
    {
        var parameters = new AcquisitionParameters(voltage, power, exposure);

        Assert.False(parameters.IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(parameters.EnsureValid);
    }
}
