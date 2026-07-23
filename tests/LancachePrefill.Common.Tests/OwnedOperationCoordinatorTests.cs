namespace LancachePrefill.Common.Tests;

public sealed class OwnedOperationCoordinatorTests
{
    [Fact]
    public async Task CancelAndWaitAsync_ImmediateCancellation_WaitsForCleanup()
    {
        await using var coordinator = new OwnedOperationCoordinator();
        var started = NewCompletionSource();
        var allowCleanup = NewCompletionSource();
        var cleanedUp = NewCompletionSource();

        await coordinator.StartAsync(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                await allowCleanup.Task;
                cleanedUp.TrySetResult();
            }
        });

        var cancelTask = coordinator.CancelAndWaitAsync();
        await started.Task;

        await Task.Yield();
        Assert.False(cancelTask.IsCompleted);

        allowCleanup.TrySetResult();
        var result = await cancelTask;

        Assert.Equal(OwnedOperationStatus.Cancelled, result.Status);
        Assert.True(cleanedUp.Task.IsCompletedSuccessfully);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WhileOperationIsRunning_IsRejected()
    {
        await using var coordinator = new OwnedOperationCoordinator();
        var started = NewCompletionSource();
        var release = NewCompletionSource();

        await coordinator.StartAsync(async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        await started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(_ => Task.CompletedTask));

        release.TrySetResult();
        Assert.Equal(OwnedOperationStatus.Completed, (await coordinator.WaitAsync()).Status);
    }

    [Fact]
    public async Task CancelAndWaitAsync_WhenIdle_IsIdempotent()
    {
        await using var coordinator = new OwnedOperationCoordinator();

        var first = await coordinator.CancelAndWaitAsync();
        var second = await coordinator.CancelAndWaitAsync();

        Assert.Equal(OwnedOperationStatus.Idle, first.Status);
        Assert.Equal(OwnedOperationStatus.Idle, second.Status);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task CancelThenStart_NewOperationRemainsOwnedByCoordinator()
    {
        await using var coordinator = new OwnedOperationCoordinator();
        var firstStarted = NewCompletionSource();
        var firstCleanupEntered = NewCompletionSource();
        var releaseFirstCleanup = NewCompletionSource();

        await coordinator.StartAsync(async cancellationToken =>
        {
            firstStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                firstCleanupEntered.TrySetResult();
                await releaseFirstCleanup.Task;
            }
        });
        await firstStarted.Task;
        var cancelFirst = coordinator.CancelAndWaitAsync();
        await firstCleanupEntered.Task;
        Assert.False(cancelFirst.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(_ => Task.CompletedTask));

        releaseFirstCleanup.TrySetResult();
        Assert.Equal(OwnedOperationStatus.Cancelled, (await cancelFirst).Status);

        var secondStarted = NewCompletionSource();
        var releaseSecond = NewCompletionSource();
        await coordinator.StartAsync(async cancellationToken =>
        {
            secondStarted.TrySetResult();
            await releaseSecond.Task.WaitAsync(cancellationToken);
        });
        await secondStarted.Task;

        await Task.Yield();
        Assert.True(coordinator.IsRunning);

        releaseSecond.TrySetResult();
        Assert.Equal(OwnedOperationStatus.Completed, (await coordinator.WaitAsync()).Status);
    }

    [Fact]
    public async Task Completion_Cancellation_AndFailureRemainDistinct()
    {
        await using var completed = new OwnedOperationCoordinator();
        await completed.StartAsync(_ => Task.CompletedTask);
        Assert.Equal(OwnedOperationStatus.Completed, (await completed.WaitAsync()).Status);

        await using var cancelled = new OwnedOperationCoordinator();
        var cancellationStarted = NewCompletionSource();
        await cancelled.StartAsync(async cancellationToken =>
        {
            cancellationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await cancellationStarted.Task;
        Assert.Equal(OwnedOperationStatus.Cancelled, (await cancelled.CancelAndWaitAsync()).Status);

        await using var failed = new OwnedOperationCoordinator();
        await failed.StartAsync(_ => Task.FromException(new InvalidDataException("failure")));
        var failedResult = await failed.WaitAsync();
        Assert.Equal(OwnedOperationStatus.Failed, failedResult.Status);
        Assert.IsType<InvalidDataException>(failedResult.Exception);
    }

    private static TaskCompletionSource NewCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
