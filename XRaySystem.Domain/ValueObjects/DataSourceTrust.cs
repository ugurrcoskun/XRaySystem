namespace XRaySystem.Domain.ValueObjects;

public enum DataSourceTrust
{
    Unverified,
    Simulation,
    Device
}

public static class DataSourceTrustClassifier
{
    public static DataSourceTrust Classify(bool isSimulator, string? sourceMarker)
    {
        if (isSimulator) return DataSourceTrust.Simulation;
        return string.IsNullOrWhiteSpace(sourceMarker)
            ? DataSourceTrust.Unverified
            : DataSourceTrust.Device;
    }
}
