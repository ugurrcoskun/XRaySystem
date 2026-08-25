using XRaySystem.Application.UseCases;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;

namespace XRaySystem.Tests;

public sealed class GetMeasurementHistoryUseCaseTests
{
    [Fact]
    public async Task History_query_passes_an_explicit_bounded_count_to_persistence()
    {
        var repository = new TrackingRepository();
        var useCase = new GetMeasurementHistoryUseCase(repository, new ProcessTelemetryUseCase());

        var result = await useCase.ExecuteAsync(maximumCount: 50);

        Assert.Empty(result);
        Assert.Equal(50, repository.RequestedMaximumCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task History_query_rejects_unbounded_counts(int maximumCount)
    {
        var repository = new TrackingRepository();
        var useCase = new GetMeasurementHistoryUseCase(repository, new ProcessTelemetryUseCase());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            useCase.ExecuteAsync(maximumCount));

        Assert.Null(repository.RequestedMaximumCount);
    }

    private sealed class TrackingRepository : IXRayMeasurementRepository
    {
        public int? RequestedMaximumCount { get; private set; }

        public Task SaveAsync(XRayMeasurement measurement) => Task.CompletedTask;

        public Task<IReadOnlyList<XRayMeasurement>> GetRecentAsync(int maximumCount)
        {
            RequestedMaximumCount = maximumCount;
            return Task.FromResult<IReadOnlyList<XRayMeasurement>>([]);
        }
    }
}
