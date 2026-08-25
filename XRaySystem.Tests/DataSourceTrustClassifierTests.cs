using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Tests;

public sealed class DataSourceTrustClassifierTests
{
    [Theory]
    [InlineData(true, null, DataSourceTrust.Simulation)]
    [InlineData(true, "mock-detector", DataSourceTrust.Simulation)]
    [InlineData(false, null, DataSourceTrust.Unverified)]
    [InlineData(false, "", DataSourceTrust.Unverified)]
    [InlineData(false, "vendor-detector:serial-001", DataSourceTrust.Device)]
    public void Classification_never_treats_simulation_or_missing_provenance_as_device(
        bool isSimulator,
        string? sourceMarker,
        DataSourceTrust expected)
    {
        Assert.Equal(expected, DataSourceTrustClassifier.Classify(isSimulator, sourceMarker));
    }
}
