namespace LancachePrefill.Common.Tests;

public sealed class RunProgressTests
{
    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("completed")]
    public async Task TerminalBeforeCommit_DoesNotInvokeCommitOrRetainSuccess(string state)
    {
        var progress = Create();
        progress.UpdateItem(new RunItemSnapshot { AppId = "a", BytesTransferred = 3 });
        Assert.True(progress.TryChooseTerminal(state));
        var before = progress.Snapshot;
        var commits = 0;

        Assert.False(progress.TryCommitItem(
            new RunItemSnapshot { AppId = "a", Result = "success", BytesTransferred = 10 },
            () => commits++));

        Assert.Equal(0, commits);
        Assert.Equal(before, progress.Snapshot);
        Assert.Null(progress.GetPage().Items[0].Result);
        await progress.CompleteAsync();
        Assert.Equal(2, progress.Snapshot.Sequence);
        Assert.Equal(0, progress.Snapshot.CompletedApps);
        Assert.Equal(3, progress.Snapshot.BytesTransferred);
        Assert.NotEqual("success", progress.GetPage().Items[0].Result);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("completed")]
    public async Task CommitBeforeTerminal_RetainsOneSuccessBeforeTheRunSettles(string state)
    {
        var progress = Create();
        progress.UpdateItem(new RunItemSnapshot { AppId = "a", BytesTransferred = 3 });
        var commits = 0;
        var item = new RunItemSnapshot { AppId = "a", Result = "success", BytesTransferred = 10 };

        Assert.True(progress.TryCommitItem(item, () =>
        {
            Assert.Equal(0, progress.Snapshot.CompletedApps);
            Assert.Equal(1, progress.Snapshot.Sequence);
            commits++;
        }));
        Assert.Equal(2, progress.Snapshot.Sequence);
        Assert.Equal(1, progress.Snapshot.CompletedApps);
        Assert.Equal(10, progress.Snapshot.BytesTransferred);
        Assert.False(progress.TryCommitItem(item, () => commits++));
        Assert.True(progress.TryChooseTerminal(state));
        Assert.False(progress.TryCommitItem(item, () => commits++));

        await progress.CompleteAsync();
        Assert.Equal(1, commits);
        Assert.Equal(3, progress.Snapshot.Sequence);
        Assert.Equal(1, progress.Snapshot.CompletedApps);
        Assert.Equal(10, progress.Snapshot.BytesTransferred);
        Assert.Equal("success", progress.GetPage().Items[0].Result);
        Assert.Equal(2, progress.GetPage().Items[0].Sequence);
        Assert.Equal(state, progress.Snapshot.State);
    }

    [Fact]
    public async Task CommitException_LeavesStateUnchangedForTheFailureFunnel()
    {
        var progress = Create();
        progress.UpdateItem(new RunItemSnapshot { AppId = "a", BytesTransferred = 3 });
        var before = progress.GetPage();
        var failure = new IOException("Atomic replacement failed.");

        var thrown = Assert.Throws<IOException>(() => progress.TryCommitItem(
            new RunItemSnapshot { AppId = "a", Result = "success", BytesTransferred = 10 },
            () => throw failure));

        Assert.Same(failure, thrown);
        Assert.Equal(before.Operation, progress.Snapshot);
        Assert.Equal(before.Items, progress.GetPage().Items);
        Assert.True(progress.UpdateItem(new RunItemSnapshot { AppId = "a", Result = "failed", BytesTransferred = 3 }));
        await progress.CompleteAsync();
        Assert.Equal("failed", progress.Snapshot.State);
        Assert.Equal(3, progress.Snapshot.Sequence);
        Assert.Equal(0, progress.Snapshot.CompletedApps);
        Assert.Equal(1, progress.Snapshot.FailedApps);
        Assert.Equal(3, progress.Snapshot.BytesTransferred);
    }

    [Fact]
    public void CommitValidation_RejectsInvalidItemsBeforeInvokingTheAction()
    {
        var progress = Create();
        progress.UpdateItem(new RunItemSnapshot { AppId = "a", BytesTransferred = 3 });
        var before = progress.Snapshot;
        var commits = 0;
        Assert.Throws<ArgumentException>(() => progress.TryCommitItem(
            new RunItemSnapshot { AppId = "missing", Result = "success" }, () => commits++));
        Assert.Throws<ArgumentException>(() => progress.TryCommitItem(
            new RunItemSnapshot { AppId = "a", Result = "success", BytesTransferred = 2 }, () => commits++));
        Assert.Throws<ArgumentException>(() => progress.TryCommitItem(
            new RunItemSnapshot { AppId = "a", BytesTransferred = 3 }, () => commits++));
        Assert.Equal(before, progress.Snapshot);
        progress.MarkCancelling();
        Assert.False(progress.TryCommitItem(
            new RunItemSnapshot { AppId = "a", Result = "success", BytesTransferred = 10 }, () => commits++));
        Assert.Equal(0, commits);
    }

    [Fact]
    public async Task TerminalContendingWithCommit_ObservesTheRetainedItemAfterCommit()
    {
        var progress = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var commit = Task.Run(() => progress.TryCommitItem(
            new RunItemSnapshot { AppId = "a", Result = "success", BytesTransferred = 10 }, () =>
            {
                entered.SetResult();
                release.Wait();
            }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = Task.Run(() =>
        {
            terminalStarted.SetResult();
            var chosen = progress.TryChooseTerminal("cancelled");
            return (chosen, snapshot: progress.Snapshot);
        });
        try
        {
            await terminalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(terminal.IsCompleted);
        }
        finally { release.Set(); }

        Assert.True(await commit);
        var result = await terminal;
        Assert.True(result.chosen);
        Assert.Equal(1, result.snapshot.Sequence);
        Assert.Equal(1, result.snapshot.CompletedApps);
        Assert.Equal(10, result.snapshot.BytesTransferred);
        await progress.CompleteAsync();
        Assert.Equal(2, progress.Snapshot.Sequence);
        Assert.Equal("success", progress.GetPage().Items[0].Result);
    }

    [Fact]
    public void SeparateTerminalCheckAndCommit_AllowsTheMarkerToOutliveCancellation()
    {
        var progress = Create();
        var allowed = progress.Terminal == null;
        progress.TryChooseTerminal("cancelled");
        var commits = 0;
        if (allowed) { commits++; }
        Assert.False(progress.UpdateItem(new RunItemSnapshot { AppId = "a", Result = "success" }));
        Assert.Equal(1, commits);
        Assert.False(progress.TryCommitItem(
            new RunItemSnapshot { AppId = "b", Result = "success" }, () => commits++));
        Assert.Equal(1, commits);
    }

    [Fact]
    public async Task TerminalChoice_RejectsLateMutationsAndPreservesPartialBytes()
    {
        var progress = Create();
        Assert.True(progress.UpdateItem(new RunItemSnapshot { AppId = "a", BytesTransferred = 12 }));
        Assert.True(progress.TryChooseTerminal("failed", "auth-lost"));
        var chosen = progress.Snapshot;
        Assert.False(progress.TryChooseTerminal("cancelled", "user"));
        progress.MarkCancelling();
        Assert.False(progress.UpdateItem(new RunItemSnapshot { AppId = "a", Result = "success", BytesTransferred = 99 }));
        Assert.Equal(chosen, progress.Snapshot);
        await progress.CompleteAsync(15, new Dictionary<string, long> { ["a"] = 15 });
        Assert.Equal("failed", progress.Snapshot.State);
        Assert.Equal("auth-lost", progress.Snapshot.Reason);
        Assert.Equal(15, progress.Snapshot.BytesTransferred);
        var terminal = progress.GetPage();
        await progress.CompleteAsync(99);
        Assert.Equal(terminal.Operation, progress.Snapshot);
        Assert.Equal(2, terminal.TotalItems);
        Assert.Equal(15, terminal.Items[0].BytesTransferred);
        Assert.All(terminal.Items, item => Assert.NotNull(item.Result));
    }

    [Fact]
    public async Task Summaries_DistinguishOverlapCachedAndFailure()
    {
        var overlap = Create();
        foreach (var id in new[] { "a", "b" })
        {
            overlap.UpdateItem(new RunItemSnapshot { AppId = id, Result = "skipped", Reason = "skippedOverlap" });
        }
        await overlap.CompleteAsync();
        Assert.Equal("completed", overlap.Snapshot.State);
        Assert.Equal("skippedOverlap", overlap.Snapshot.Reason);
        Assert.Equal(2, overlap.Snapshot.SkippedApps);
        Assert.Equal(0, overlap.Snapshot.CachedApps);
        var failed = Create();
        failed.UpdateItem(new RunItemSnapshot { AppId = "a", Result = "already_cached" });
        failed.UpdateItem(new RunItemSnapshot { AppId = "b", Result = "failed", BytesTransferred = 5 });
        await failed.CompleteAsync();
        Assert.Equal("failed", failed.Snapshot.State);
        Assert.Equal(1, failed.Snapshot.FailedApps);
        Assert.Equal(1, failed.Snapshot.CachedApps);
        Assert.Equal(5, failed.Snapshot.BytesTransferred);
    }

    [Fact]
    public async Task SlowSubscriber_CoalescesTicksAndRetainsEveryItemForRecovery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<RunSnapshot>();
        var progress = Create(async (snapshot, _) =>
        {
            sent.Add(snapshot);
            entered.TrySetResult();
            await release.Task;
        });
        progress.UpdateItem(new RunItemSnapshot { AppId = "a", BytesTransferred = 1 });
        await entered.Task;
        for (var count = 2; count <= 1000; count++)
        {
            progress.UpdateItem(new RunItemSnapshot { AppId = "a", BytesTransferred = count });
        }
        progress.UpdateItem(new RunItemSnapshot { AppId = "a", BytesTransferred = 1000, Result = "success" });
        progress.UpdateItem(new RunItemSnapshot { AppId = "b", Result = "already_cached" });
        var completion = progress.CompleteAsync();
        Assert.False(completion.IsCompleted);
        release.SetResult();
        await completion;
        Assert.Equal(2, sent.Count);
        Assert.True(sent[0].Sequence < sent[1].Sequence);
        Assert.Equal("completed", sent[1].State);
        var first = progress.GetPage(0, 1);
        var second = progress.GetPage(first.NextOffset!.Value, 1);
        Assert.Equal("success", Assert.Single(first.Items).Result);
        Assert.Equal("already_cached", Assert.Single(second.Items).Result);
        Assert.Null(second.NextOffset);
        Assert.Equal(first.Operation, second.Operation);
        Assert.Null(first.Options.AppIds);
        Assert.Empty(first.Options.CachedDepots);
    }

    [Fact]
    public async Task SubscriberTimeout_DetachesWithoutBlockingRunDrain()
    {
        var forever = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var progress = new RunProgress("run", "boot", new RunOptions { AppIds = ["a"], MaxConcurrency = 1 },
            (_, _) => { Interlocked.Increment(ref calls); return forever.Task; }, sendTimeout: TimeSpan.Zero);
        progress.UpdateItem(new RunItemSnapshot { AppId = "a", Result = "success" });
        await progress.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal("completed", progress.GetPage().Operation.State);
        forever.SetResult();
    }

    [Fact]
    public void UnresolvedSelection_IsExplicitAndOnlyResolvedOnce()
    {
        var progress = new RunProgress("run", "boot", new RunOptions { Selection = "all", MaxConcurrency = 1 });
        Assert.False(progress.GetPage().SelectionResolved);
        progress.ResolveSelection(["b", "a", "b"]);
        Assert.Equal(new[] { "b", "a" }, progress.GetPage().Items.Select(item => item.AppId));
        Assert.Throws<InvalidOperationException>(() => progress.ResolveSelection(["c"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.GetPage(0, 201));
    }

    private static RunProgress Create(Func<RunSnapshot, CancellationToken, Task>? publish = null)
        => new("run", "boot", new RunOptions { AppIds = ["a", "b"], MaxConcurrency = 1 }, publish);

    [Fact]
    public async Task ConcurrentTerminalContenders_OnlyOneCanChooseTheOutcome()
    {
        var progress = Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = Enumerable.Range(0, 16).Select(async index =>
        {
            await release.Task;
            return (won: progress.TryChooseTerminal(index % 2 == 0 ? "failed" : "cancelled", $"reason{index}"), index);
        }).ToArray();
        release.SetResult();
        var results = await Task.WhenAll(contenders);
        var winner = Assert.Single(results, result => result.won);
        await progress.CompleteAsync();
        Assert.Equal($"reason{winner.index}", progress.Snapshot.Reason);
        var snapshot = progress.Snapshot;
        progress.MarkCancelling();
        Assert.Equal(snapshot, progress.Snapshot);
    }
}
