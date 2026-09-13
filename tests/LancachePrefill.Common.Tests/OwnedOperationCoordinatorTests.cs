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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task KeyedAdmission_EnforcesCapacityAndReplaysBeforeCapacity(int limit)
    {
        await using var owner = new OwnedOperationCoordinator(limit);
        var release = NewCompletionSource();
        for (var index = 0; index < limit; index++)
        {
            var id = $"run{index}";
            var progress = Progress(id);
            var admitted = await owner.StartAsync(id, "same", progress, token => release.Task.WaitAsync(token));
            Assert.True(admitted.Accepted);
            Assert.NotNull(owner.GetOperation(id));
        }
        Assert.Equal(limit, owner.GetActiveOperations().Count);
        var replay = await owner.StartAsync("run0", "same", Progress("run0"), _ => throw new InvalidOperationException());
        Assert.True(replay.Replayed);
        var conflict = await owner.StartAsync("run0", "changed", Progress("run0"), _ => Task.CompletedTask);
        Assert.Equal("operation-conflict", conflict.Error);
        var full = await owner.StartAsync("extra", "same", Progress("extra"), _ => Task.CompletedTask);
        Assert.Equal("run-limit", full.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync(_ => Task.CompletedTask));
        release.SetResult();
        for (var index = 0; index < limit; index++) { await owner.WaitAsync($"run{index}"); }
        Assert.Empty(owner.GetActiveOperations());
        Assert.Equal(limit, owner.GetRecentOperations().Count);
        Assert.True((await owner.StartAsync("run0", "same", Progress("run0"), _ => throw new InvalidOperationException())).Replayed);
    }

    [Fact]
    public async Task TargetedCancel_DrainsOnlyItsRunAndRejectsAmbiguity()
    {
        await using var owner = new OwnedOperationCoordinator(3);
        var entered = new[] { NewCompletionSource(), NewCompletionSource(), NewCompletionSource() };
        var release = NewCompletionSource();
        var cleanup = NewCompletionSource();
        for (var index = 0; index < 3; index++)
        {
            var slot = index;
            await owner.StartAsync($"run{slot}", "same", Progress($"run{slot}"), async token =>
            {
                entered[slot].SetResult();
                try { await release.Task.WaitAsync(token); }
                finally { if (slot == 0) { await cleanup.Task; } }
            });
        }
        await Task.WhenAll(entered.Select(item => item.Task));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CancelAndWaitAsync());
        Assert.Throws<InvalidOperationException>(() => owner.Cancel("run0", "wrong"));
        Assert.Null(owner.Cancel("missing", "boot"));
        Assert.Equal("cancelling", owner.Cancel("run0", "boot")!.State);
        Assert.Equal("cancelling", owner.Cancel("run0", "boot")!.State);
        Assert.False(owner.WaitAsync("run0").IsCompleted);
        Assert.Equal(3, owner.GetActiveOperations().Count);
        cleanup.SetResult();
        Assert.Equal(OwnedOperationStatus.Cancelled, (await owner.WaitAsync("run0")).Status);
        Assert.Equal(2, owner.GetActiveOperations().Count);
        release.SetResult();
        Assert.Equal(OwnedOperationStatus.Completed, (await owner.WaitAsync("run1")).Status);
        Assert.Equal(OwnedOperationStatus.Completed, (await owner.WaitAsync("run2")).Status);
        Assert.Equal("cancelled", owner.Cancel("run0", "boot")!.State);
    }

    [Fact]
    public async Task Retention_EvictsWholeTerminalOperationsByAgeCountAndItems()
    {
        var now = DateTimeOffset.UtcNow;
        await using var owner = new OwnedOperationCoordinator(4, () => now);
        for (var index = 0; index <= 256; index++)
        {
            var id = $"run{index}";
            await owner.StartAsync(id, "same", Progress(id), _ => Task.CompletedTask);
            await owner.WaitAsync(id);
        }
        Assert.Equal(256, owner.GetRecentOperations().Count);
        Assert.Null(owner.GetOperation("run0"));
        now = now.AddHours(25);
        Assert.Empty(owner.GetRecentOperations());
        var many = new RunProgress("many", "boot", new RunOptions
        {
            AppIds = Enumerable.Range(0, 10001).Select(index => index.ToString()).ToArray(),
            MaxConcurrency = 1
        });
        var release = NewCompletionSource();
        await owner.StartAsync("many", "same", many, _ => release.Task);
        var wait = owner.WaitAsync("many");
        release.SetResult();
        await wait;
        Assert.Null(owner.GetOperation("many"));
    }

    [Fact]
    public async Task Dispose_ClosesAdmissionAndBothCallersWaitForCleanup()
    {
        var owner = new OwnedOperationCoordinator();
        var entered = NewCompletionSource();
        var cleanup = NewCompletionSource();
        await owner.StartAsync("run", "same", Progress("run"), async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await cleanup.Task; }
        });
        await entered.Task;
        var first = owner.DisposeAsync().AsTask();
        var second = owner.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.StartAsync(_ => Task.CompletedTask));
        cleanup.SetResult();
        await Task.WhenAll(first, second);
    }

    private static RunProgress Progress(string id)
        => new(id, "boot", new RunOptions { AppIds = ["a"], MaxConcurrency = 1 });

    [Fact]
    public async Task CancellationCallbacks_DoNotDelayAcknowledgementButRemainOwnedUntilDrained()
    {
        await using var owner = new OwnedOperationCoordinator(1);
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = NewCompletionSource();
        var started = NewCompletionSource();
        await owner.StartAsync("run", "same", Progress("run"), async token =>
        {
            using var callback = token.Register(() =>
            {
                callbackEntered.SetResult();
                releaseCallback.Wait();
            });
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await started.Task;
        try
        {
            Assert.Equal("cancelling", owner.Cancel("run", "boot")!.State);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(owner.WaitAsync("run").IsCompleted);
            Assert.Single(owner.GetActiveOperations());
        }
        finally { releaseCallback.Set(); }
        Assert.Equal(OwnedOperationStatus.Cancelled, (await owner.WaitAsync("run")).Status);
    }
}
