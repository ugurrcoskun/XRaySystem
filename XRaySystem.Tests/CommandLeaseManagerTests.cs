using XRaySystem.DeviceHost;

namespace XRaySystem.Tests;

public sealed class CommandLeaseManagerTests
{
    [Fact]
    public void Lease_allows_owner_renewal_blocks_other_client_and_expires()
    {
        var lease = new CommandLeaseManager(TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;

        Assert.True(lease.TryAcquire("operator-a", now));
        Assert.True(lease.TryAcquire("operator-a", now.AddSeconds(10)));
        Assert.False(lease.TryAcquire("operator-b", now.AddSeconds(20)));
        Assert.True(lease.TryAcquire("operator-b", now.AddSeconds(41)));
    }

    [Fact]
    public void Lease_can_only_be_released_by_its_owner()
    {
        var lease = new CommandLeaseManager(TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;
        Assert.True(lease.TryAcquire("operator-a", now));

        lease.Release("operator-b");
        Assert.False(lease.TryAcquire("operator-b", now.AddSeconds(1)));

        lease.Release("operator-a");
        Assert.True(lease.TryAcquire("operator-b", now.AddSeconds(2)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("client\nspoof")]
    public void Lease_rejects_empty_or_log_injection_client_ids(string clientId)
    {
        var lease = new CommandLeaseManager(TimeSpan.FromSeconds(30));

        Assert.Throws<ArgumentException>(() => lease.TryAcquire(clientId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Lease_rejects_overlong_client_ids()
    {
        var lease = new CommandLeaseManager(TimeSpan.FromSeconds(30));

        Assert.Throws<ArgumentException>(() =>
            lease.TryAcquire(new string('x', 129), DateTimeOffset.UtcNow));
    }
}
