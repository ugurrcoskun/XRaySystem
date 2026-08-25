using System.Collections.Concurrent;
using XRaySystem.Infrastructure.Concurrency;

namespace XRaySystem.Tests;

public sealed class LatestValueWorkerTests
{
    [Fact]
    public async Task Busy_worker_keeps_only_the_newest_pending_value()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<int>();
        using var worker = new LatestValueWorker<int>(async (value, cancellationToken) =>
        {
            processed.Enqueue(value);
            if (value == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            if (value == 4) latestCompleted.TrySetResult();
        });

        Assert.True(worker.TryPost(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(worker.TryPost(2));
        Assert.True(worker.TryPost(3));
        Assert.True(worker.TryPost(4));
        releaseFirst.TrySetResult();
        await latestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 4], processed.ToArray());
        Assert.Equal(4, worker.AcceptedCount);
        Assert.Equal(2, worker.ReplacedCount);
        Assert.Equal(2, worker.CompletedCount);
        Assert.Equal(0, worker.FaultedCount);
    }

    [Fact]
    public async Task Processor_fault_is_counted_and_does_not_stop_the_next_value()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        using var worker = new LatestValueWorker<int>(
            (value, _) =>
            {
                if (value == 1) throw new InvalidDataException("bad frame");
                completed.TrySetResult();
                return ValueTask.CompletedTask;
            },
            (_, error) => errors.Enqueue(error));

        Assert.True(worker.TryPost(1));
        for (var attempt = 0; attempt < 100 && worker.FaultedCount == 0; attempt++)
            await Task.Delay(5);
        Assert.True(worker.TryPost(2));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, worker.FaultedCount);
        Assert.Equal(1, worker.CompletedCount);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
    }

    [Fact]
    public void Disposed_worker_rejects_new_values()
    {
        var worker = new LatestValueWorker<int>((_, _) => ValueTask.CompletedTask);

        worker.Dispose();

        Assert.False(worker.TryPost(1));
        Assert.Equal(0, worker.AcceptedCount);
    }
}
