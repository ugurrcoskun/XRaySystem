using XRaySystem.Domain.WindowLevel;

namespace XRaySystem.Tests;

public sealed class WindowLevelMapperTests
{
    [Fact]
    public void Full_range_maps_gray16_to_gray8_without_mutating_source()
    {
        ushort[] source = [0, 32768, ushort.MaxValue];
        var original = source.ToArray();

        var mapped = WindowLevelMapper.Map(source, 32767.5, 65535);

        Assert.Equal([0, 128, 255], mapped);
        Assert.Equal(original, source);
    }

    [Fact]
    public void Auto_uses_one_and_ninety_ninth_percentiles()
    {
        ushort[] source = Enumerable.Range(0, 100).Select(value => (ushort)(value * 100)).ToArray();

        var result = WindowLevelMapper.Auto(source);

        Assert.Equal(4900, result.Center);
        Assert.Equal(9800, result.Width);
    }
}
