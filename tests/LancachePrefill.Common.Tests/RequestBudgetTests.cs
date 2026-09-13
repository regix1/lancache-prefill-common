namespace LancachePrefill.Common.Tests;

public sealed class RequestBudgetTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(16)]
    public async Task Grants_OneTurnPerReadyRunBeforeRepeating(int runCount)
    {
        using var budget = new RequestBudget(1);
        var held = await budget.AcquireAsync("held", 1);
        var first = Enumerable.Range(0, runCount).Select(index => budget.AcquireAsync($"run{index}", 1)).ToArray();
        var second = Enumerable.Range(0, runCount).Select(index => budget.AcquireAsync($"run{index}", 1)).ToArray();
        held.Dispose();
        for (var index = 0; index < runCount; index++)
        {
            var lease = await first[index].WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(second, task => Assert.False(task.IsCompleted));
            Assert.Equal(1, budget.ActiveRequests);
            lease.Dispose();
            lease.Dispose();
        }
        for (var index = 0; index < runCount; index++)
        {
            (await second[index].WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        }
        Assert.Equal(0, budget.ActiveRequests);
    }

    [Fact]
    public async Task PerRunCeiling_DoesNotBlockEligibleSiblings()
    {
        using var budget = new RequestBudget(3);
        using var a = await budget.AcquireAsync("a", 1);
        var waitingA = budget.AcquireAsync("a", 1);
        using var b = await budget.AcquireAsync("b", 2);
        using var c = await budget.AcquireAsync("c", 3);
        Assert.False(waitingA.IsCompleted);
        Assert.Equal(3, budget.ActiveRequests);
        a.Dispose();
        using var next = await waitingA;
        Assert.Equal(3, budget.ActiveRequests);
    }

    [Fact]
    public async Task Cancellation_RemovesQueuedTurnAndDisposalFailsPendingWaiters()
    {
        using var budget = new RequestBudget(1);
        var held = await budget.AcquireAsync("held", 1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = budget.AcquireAsync("a", 1, cancellation.Token);
        var sibling = budget.AcquireAsync("b", 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        held.Dispose();
        using var granted = await sibling;
        var pending = budget.AcquireAsync("c", 1);
        budget.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        granted.Dispose();
        Assert.Equal(0, budget.ActiveRequests);
    }
}
