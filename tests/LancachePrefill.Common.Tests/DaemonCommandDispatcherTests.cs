using System.Collections.Concurrent;
using System.Diagnostics;

namespace LancachePrefill.Common.Tests;

public sealed class DaemonCommandDispatcherTests
{
    [Fact]
    public async Task BlockedConcurrentCommand_DoesNotBlockControlResponse()
    {
        await using var dispatcher = new DaemonCommandDispatcher(maxConcurrentHandlers: 1);
        await using var client = dispatcher.CreateClient();
        var longStarted = NewCompletionSource();
        var releaseLong = NewCompletionSource();
        var controlResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await client.DispatchAsync(
            "long-1",
            DaemonCommandLane.Concurrent,
            async cancellationToken =>
            {
                longStarted.TrySetResult();
                await releaseLong.Task.WaitAsync(cancellationToken);
                return "long";
            },
            (_, _, _) => Task.CompletedTask);
        await longStarted.Task;

        await client.DispatchAsync(
            "control-2",
            DaemonCommandLane.Control,
            _ => Task.FromResult("status"),
            (requestId, _, _) =>
            {
                controlResponse.TrySetResult(requestId);
                return Task.CompletedTask;
            });

        Assert.Equal("control-2", await controlResponse.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        releaseLong.TrySetResult();
        Assert.True(await client.DisconnectAsync());
    }

    [Fact]
    public async Task SerializedCommands_DoNotOverlap()
    {
        await using var dispatcher = new DaemonCommandDispatcher(maxConcurrentHandlers: 2);
        await using var client = dispatcher.CreateClient();
        var firstStarted = NewCompletionSource();
        var releaseFirst = NewCompletionSource();
        var secondStarted = NewCompletionSource();

        await client.DispatchAsync(
            "mutate-1",
            DaemonCommandLane.Serialized,
            async cancellationToken =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return true;
            },
            (_, _, _) => Task.CompletedTask);
        await firstStarted.Task;

        await client.DispatchAsync(
            "mutate-2",
            DaemonCommandLane.Serialized,
            _ =>
            {
                secondStarted.TrySetResult();
                return Task.FromResult(true);
            },
            (_, _, _) => Task.CompletedTask);

        await Task.Delay(50);
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await client.DisconnectAsync());
    }

    [Fact]
    public async Task Disconnect_CancelsAndDrainsClientHandlers()
    {
        await using var dispatcher = new DaemonCommandDispatcher();
        await using var client = dispatcher.CreateClient();
        var started = NewCompletionSource();
        var cleanedUp = NewCompletionSource();

        await client.DispatchAsync(
            "read-1",
            DaemonCommandLane.Concurrent,
            async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    cleanedUp.TrySetResult();
                }

                return true;
            },
            (_, _, _) => Task.CompletedTask);
        await started.Task;

        Assert.True(await client.DisconnectAsync());
        Assert.True(cleanedUp.Task.IsCompletedSuccessfully);
        Assert.Equal(0, client.ActiveHandlerCount);
    }

    [Fact]
    public async Task HandlerExceptions_AreObserved()
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dispatcher = new DaemonCommandDispatcher(
            exceptionObserver: exception => observed.TrySetResult(exception));
        await using var client = dispatcher.CreateClient();

        await client.DispatchAsync<string>(
            "fault-1",
            DaemonCommandLane.Concurrent,
            _ => Task.FromException<string>(new InvalidOperationException("handler failed")),
            (_, _, _) => Task.CompletedTask);

        var exception = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<InvalidOperationException>(exception);
        Assert.True(await client.DisconnectAsync());
    }

    [Fact]
    public async Task ConcurrentResponses_PreserveRequestIdsWhenCompletionOrderDiffers()
    {
        await using var dispatcher = new DaemonCommandDispatcher(maxConcurrentHandlers: 2);
        await using var client = dispatcher.CreateClient();
        var firstStarted = NewCompletionSource();
        var secondStarted = NewCompletionSource();
        var releaseFirst = NewCompletionSource();
        var releaseSecond = NewCompletionSource();
        var responses = new ConcurrentQueue<string>();
        var allResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task RecordResponseAsync(string requestId, string _, CancellationToken __)
        {
            responses.Enqueue(requestId);
            if (responses.Count == 2)
            {
                allResponses.TrySetResult();
            }

            return Task.CompletedTask;
        }

        await client.DispatchAsync(
            "request-1",
            DaemonCommandLane.Concurrent,
            async cancellationToken =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return "first";
            },
            RecordResponseAsync);
        await client.DispatchAsync(
            "request-2",
            DaemonCommandLane.Concurrent,
            async cancellationToken =>
            {
                secondStarted.TrySetResult();
                await releaseSecond.Task.WaitAsync(cancellationToken);
                return "second";
            },
            RecordResponseAsync);

        await Task.WhenAll(firstStarted.Task, secondStarted.Task);
        releaseSecond.TrySetResult();
        await WaitUntilAsync(() => responses.Count == 1);
        releaseFirst.TrySetResult();
        await allResponses.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "request-2", "request-1" }, responses);
        Assert.True(await client.DisconnectAsync());
    }

    [Fact]
    public async Task StopAsync_ReturnsWithinDrainTimeoutWhenHandlerIgnoresCancellation()
    {
        await using var dispatcher = new DaemonCommandDispatcher(drainTimeout: TimeSpan.FromMilliseconds(75));
        await using var client = dispatcher.CreateClient();
        var started = NewCompletionSource();
        var release = NewCompletionSource();

        await client.DispatchAsync(
            "blocked-1",
            DaemonCommandLane.Concurrent,
            async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return true;
            },
            (_, _, _) => Task.CompletedTask);
        await started.Task;

        var stopwatch = Stopwatch.StartNew();
        var drained = await dispatcher.StopAsync();
        stopwatch.Stop();

        Assert.False(drained);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));

        release.TrySetResult();
        Assert.True(await dispatcher.StopAsync());
    }

    [Fact]
    public async Task StopAsync_CancelsAndDrainsDispatchWaitingForCapacity()
    {
        await using var dispatcher = new DaemonCommandDispatcher(
            maxConcurrentHandlers: 1,
            drainTimeout: TimeSpan.FromSeconds(2));
        await using var client = dispatcher.CreateClient();
        var firstStarted = NewCompletionSource();

        await client.DispatchAsync(
            "active-1",
            DaemonCommandLane.Concurrent,
            async cancellationToken =>
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
            (_, _, _) => Task.CompletedTask);
        await firstStarted.Task;

        var waitingDispatch = client.DispatchAsync(
            "waiting-2",
            DaemonCommandLane.Concurrent,
            _ => Task.FromResult(true),
            (_, _, _) => Task.CompletedTask);
        await Task.Delay(25);
        Assert.False(waitingDispatch.IsCompleted);

        Assert.True(await dispatcher.StopAsync());
        var exception = await Record.ExceptionAsync(() => waitingDispatch);
        Assert.True(
            exception is OperationCanceledException or InvalidOperationException,
            exception?.ToString());
        Assert.Equal(0, dispatcher.ActiveHandlerCount);
    }

    private static TaskCompletionSource NewCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
