using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

#nullable enable

namespace LancachePrefill.Common;

public enum DaemonCommandLane
{
    Control,
    Concurrent,
    Serialized
}

public sealed class DaemonCommandDispatcher : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly object _admissionSync = new();
    private readonly ConcurrentDictionary<long, Task> _activeHandlers = new();
    private readonly ConcurrentDictionary<long, DaemonCommandClient> _clients = new();
    private readonly SemaphoreSlim _handlerSlots;
    private readonly SemaphoreSlim _controlSlot = new(1, 1);
    private readonly SemaphoreSlim _serializedGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly TimeSpan _drainTimeout;
    private readonly Action<Exception>? _exceptionObserver;
    private Task _shutdownCancellationTask = Task.CompletedTask;
    private Task? _deferredDisposalTask;
    private long _nextClientId;
    private long _nextHandlerId;
    private int _stopping;
    private int _disposed;
    private int _resourcesDisposed;
    private int _activeDispatchCalls;
    private TaskCompletionSource? _dispatchDrainCompletion;

    public DaemonCommandDispatcher(
        int maxConcurrentHandlers = 8,
        TimeSpan? drainTimeout = null,
        Action<Exception>? exceptionObserver = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentHandlers);

        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        if (_drainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        }

        _handlerSlots = new SemaphoreSlim(maxConcurrentHandlers, maxConcurrentHandlers);
        _exceptionObserver = exceptionObserver;
    }

    public int ActiveHandlerCount => _activeHandlers.Count;

    public DaemonCommandClient CreateClient(CancellationToken clientLifetime = default)
    {
        lock (_admissionSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _stopping) != 0)
            {
                throw new InvalidOperationException("The dispatcher is stopping.");
            }

            var clientId = Interlocked.Increment(ref _nextClientId);
            var client = new DaemonCommandClient(
                this,
                clientId,
                clientLifetime,
                _shutdownCancellation.Token);
            if (!_clients.TryAdd(clientId, client))
            {
                client.DisposeCancellation();
                throw new InvalidOperationException("Unable to register the client dispatcher.");
            }

            return client;
        }
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        DaemonCommandClient[] clients;
        Task[] handlers;
        lock (_admissionSync)
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 0)
            {
                _shutdownCancellationTask = _shutdownCancellation.CancelAsync();
            }

            clients = _clients.Values.ToArray();
            var activeHandlers = _activeHandlers.Values.ToArray();
            var dispatchesDrained = _activeDispatchCalls == 0
                ? Task.CompletedTask
                : (_dispatchDrainCompletion ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            handlers = new Task[activeHandlers.Length + 1];
            activeHandlers.CopyTo(handlers, 0);
            handlers[^1] = dispatchesDrained;
        }

        var drainTasks = new List<Task>(handlers) { _shutdownCancellationTask };
        foreach (var client in clients)
        {
            _ = client.BeginDisconnect(out var cancellationTask);
            drainTasks.Add(cancellationTask);
        }

        var drained = await WaitForHandlersAsync(
            drainTasks.ToArray(),
            cancellationToken).ConfigureAwait(false);

        if (drained)
        {
            foreach (var client in clients)
            {
                CompleteClientDisconnect(client);
            }
        }

        return drained;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (await StopAsync().ConfigureAwait(false))
        {
            DisposeResources();
            return;
        }

        _deferredDisposalTask = DisposeWhenHandlersCompleteAsync();
    }

    internal async Task DispatchAsync<TResponse>(
        DaemonCommandClient client,
        string requestId,
        DaemonCommandLane lane,
        Func<CancellationToken, Task<TResponse>> handler,
        Func<string, TResponse, CancellationToken, Task> sendResponseAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(sendResponseAsync);
        if (!Enum.IsDefined(lane))
        {
            throw new ArgumentOutOfRangeException(nameof(lane));
        }

        var clientCancellationToken = client.GetCancellationTokenOrThrow();
        BeginDispatchAdmission();
        try
        {
            await DispatchAdmittedAsync(
                client,
                requestId,
                lane,
                handler,
                sendResponseAsync,
                clientCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteDispatchAdmission();
        }
    }

    private async Task DispatchAdmittedAsync<TResponse>(
        DaemonCommandClient client,
        string requestId,
        DaemonCommandLane lane,
        Func<CancellationToken, Task<TResponse>> handler,
        Func<string, TResponse, CancellationToken, Task> sendResponseAsync,
        CancellationToken clientCancellationToken)
    {
        var slot = lane == DaemonCommandLane.Control ? _controlSlot : _handlerSlots;
        await slot.WaitAsync(clientCancellationToken).ConfigureAwait(false);

        var startGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerId = Interlocked.Increment(ref _nextHandlerId);
        var task = ExecuteHandlerAsync(
            startGate.Task,
            client,
            handlerId,
            requestId,
            lane,
            handler,
            sendResponseAsync,
            slot,
            clientCancellationToken);

        var accepted = false;
        var dispatcherStopping = false;
        lock (_admissionSync)
        {
            dispatcherStopping = Volatile.Read(ref _stopping) != 0;
            if (!dispatcherStopping && client.TryTrack(handlerId, task))
            {
                _activeHandlers[handlerId] = task;
                accepted = true;
            }
        }

        if (accepted)
        {
            startGate.TrySetResult();
            return;
        }

        startGate.TrySetCanceled(new CancellationToken(canceled: true));
        await task.ConfigureAwait(false);
        if (dispatcherStopping)
        {
            throw new InvalidOperationException("The dispatcher is stopping.");
        }

        clientCancellationToken.ThrowIfCancellationRequested();
        throw new ObjectDisposedException(nameof(DaemonCommandClient));
    }

    private void BeginDispatchAdmission()
    {
        lock (_admissionSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _stopping) != 0)
            {
                throw new InvalidOperationException("The dispatcher is stopping.");
            }

            _activeDispatchCalls++;
        }
    }

    private void CompleteDispatchAdmission()
    {
        TaskCompletionSource? dispatchesDrained = null;
        lock (_admissionSync)
        {
            _activeDispatchCalls--;
            if (_activeDispatchCalls == 0 && Volatile.Read(ref _stopping) != 0)
            {
                dispatchesDrained = _dispatchDrainCompletion;
            }
        }

        dispatchesDrained?.TrySetResult();
    }

    private async Task ExecuteHandlerAsync<TResponse>(
        Task startGate,
        DaemonCommandClient client,
        long handlerId,
        string requestId,
        DaemonCommandLane lane,
        Func<CancellationToken, Task<TResponse>> handler,
        Func<string, TResponse, CancellationToken, Task> sendResponseAsync,
        SemaphoreSlim slot,
        CancellationToken clientCancellationToken)
    {
        var serializedGateEntered = false;
        try
        {
            await startGate.ConfigureAwait(false);
            if (lane == DaemonCommandLane.Serialized)
            {
                await _serializedGate.WaitAsync(clientCancellationToken).ConfigureAwait(false);
                serializedGateEntered = true;
            }

            var response = await handler(clientCancellationToken).ConfigureAwait(false);
            await sendResponseAsync(
                requestId,
                response,
                clientCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            startGate.IsCanceled || clientCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _exceptionObserver?.Invoke(exception);
        }
        finally
        {
            if (serializedGateEntered)
            {
                _serializedGate.Release();
            }

            slot.Release();
            client.Untrack(handlerId);
            _activeHandlers.TryRemove(handlerId, out _);
        }
    }

    internal async Task<bool> DisconnectClientAsync(
        DaemonCommandClient client,
        CancellationToken cancellationToken)
    {
        var handlers = client.BeginDisconnect(out var cancellationTask);
        var drainTasks = new Task[handlers.Length + 1];
        handlers.CopyTo(drainTasks, 0);
        drainTasks[^1] = cancellationTask;
        var drained = await WaitForHandlersAsync(drainTasks, cancellationToken).ConfigureAwait(false);
        if (drained)
        {
            CompleteClientDisconnect(client);
        }

        return drained;
    }

    private async Task<bool> WaitForHandlersAsync(
        Task[] handlers,
        CancellationToken cancellationToken)
    {
        if (handlers.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(handlers)
                .WaitAsync(_drainTimeout, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void CompleteClientDisconnect(DaemonCommandClient client)
    {
        _clients.TryRemove(client.Id, out _);
        client.DisposeCancellation();
    }

    private async Task DisposeWhenHandlersCompleteAsync()
    {
        Task dispatchesDrained;
        DaemonCommandClient[] clients;
        lock (_admissionSync)
        {
            dispatchesDrained = _activeDispatchCalls == 0
                ? Task.CompletedTask
                : (_dispatchDrainCompletion ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            clients = _clients.Values.ToArray();
        }

        var drainTasks = new List<Task>(_activeHandlers.Values)
        {
            dispatchesDrained,
            _shutdownCancellationTask
        };
        foreach (var client in clients)
        {
            _ = client.BeginDisconnect(out var cancellationTask);
            drainTasks.Add(cancellationTask);
        }

        await Task.WhenAll(drainTasks).ConfigureAwait(false);
        foreach (var client in clients)
        {
            CompleteClientDisconnect(client);
        }

        DisposeResources();
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _shutdownCancellation.Dispose();
        _handlerSlots.Dispose();
        _controlSlot.Dispose();
        _serializedGate.Dispose();
    }

}

public sealed class DaemonCommandClient : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly DaemonCommandDispatcher _owner;
    private readonly CancellationTokenSource _cancellation;
    private readonly ConcurrentDictionary<long, Task> _activeHandlers = new();
    private Task _cancellationTask = Task.CompletedTask;
    private bool _accepting = true;
    private int _cancellationDisposed;

    internal DaemonCommandClient(
        DaemonCommandDispatcher owner,
        long id,
        CancellationToken clientLifetime,
        CancellationToken dispatcherLifetime)
    {
        _owner = owner;
        Id = id;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            clientLifetime,
            dispatcherLifetime);
    }

    internal long Id { get; }

    public int ActiveHandlerCount => _activeHandlers.Count;

    public Task DispatchAsync<TResponse>(
        string requestId,
        DaemonCommandLane lane,
        Func<CancellationToken, Task<TResponse>> handler,
        Func<string, TResponse, CancellationToken, Task> sendResponseAsync)
        => _owner.DispatchAsync(this, requestId, lane, handler, sendResponseAsync);

    public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
        => _owner.DisconnectClientAsync(this, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (await DisconnectAsync().ConfigureAwait(false)
            && Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
        {
            _cancellation.Dispose();
        }
    }

    internal bool TryTrack(long handlerId, Task handler)
    {
        lock (_sync)
        {
            if (!_accepting)
            {
                return false;
            }

            return _activeHandlers.TryAdd(handlerId, handler);
        }
    }

    internal void Untrack(long handlerId)
        => _activeHandlers.TryRemove(handlerId, out _);

    internal Task[] BeginDisconnect(out Task cancellationTask)
    {
        lock (_sync)
        {
            if (_accepting)
            {
                _accepting = false;
                _cancellationTask = _cancellation.CancelAsync();
            }

            cancellationTask = _cancellationTask;
            return _activeHandlers.Values.ToArray();
        }
    }

    internal CancellationToken GetCancellationTokenOrThrow()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            _cancellation.Token.ThrowIfCancellationRequested();
            return _cancellation.Token;
        }
    }

    internal void DisposeCancellation()
    {
        if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
        {
            _cancellation.Dispose();
        }
    }
}
