using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

#nullable enable

namespace LancachePrefill.Common;

public sealed class RequestBudget : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RequestQueue> _runs = new(StringComparer.Ordinal);
    private readonly LinkedList<RequestQueue> _turns = new();
    private int _active;
    private bool _disposed;

    public RequestBudget(int maxConcurrentRequests)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentRequests, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrentRequests, 128);
        MaxConcurrentRequests = maxConcurrentRequests;
    }

    public int MaxConcurrentRequests { get; }
    public int ActiveRequests { get { lock (_sync) { return _active; } } }

    public async Task<IDisposable> AcquireAsync(string operationId, int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        cancellationToken.ThrowIfCancellationRequested();
        RequestWait wait;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var limit = Math.Min(maxConcurrency, MaxConcurrentRequests);
            if (!_runs.TryGetValue(operationId, out var queue))
            {
                queue = new RequestQueue { OperationId = operationId, Limit = limit };
                _runs.Add(operationId, queue);
            }
            else if (queue.Limit != limit)
            {
                throw new InvalidOperationException("An active run cannot change its request ceiling.");
            }
            wait = new RequestWait { Queue = queue, Cancellation = cancellationToken };
            wait.Node = queue.Waiters.AddLast(wait);
            queue.Turn ??= _turns.AddLast(queue);
            Grant();
        }
        using var cancellation = cancellationToken.Register(() => Cancel(wait));
        return await wait.Completion.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var queue in _runs.Values)
            {
                foreach (var wait in queue.Waiters)
                {
                    wait.Node = null;
                    wait.Completion.TrySetException(new ObjectDisposedException(nameof(RequestBudget)));
                }
                queue.Waiters.Clear();
                queue.Turn = null;
            }
            _turns.Clear();
        }
    }

    private void Cancel(RequestWait wait)
    {
        lock (_sync)
        {
            if (wait.Node == null) { return; }
            wait.Queue.Waiters.Remove(wait.Node);
            wait.Node = null;
            wait.Completion.TrySetCanceled(wait.Cancellation);
            RemoveEmpty(wait.Queue);
            Grant();
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The successful waiter takes ownership of the lease and returns its permit on disposal.")]
    private void Grant()
    {
        var skipped = 0;
        while (!_disposed && _active < MaxConcurrentRequests && _turns.First != null)
        {
            var queue = _turns.First.Value;
            _turns.RemoveFirst();
            queue.Turn = _turns.AddLast(queue);
            if (queue.Active >= queue.Limit)
            {
                if (++skipped >= _turns.Count) { return; }
                continue;
            }
            skipped = 0;
            var wait = queue.Waiters.First!.Value;
            queue.Waiters.RemoveFirst();
            wait.Node = null;
            if (wait.Cancellation.IsCancellationRequested)
            {
                wait.Completion.TrySetCanceled(wait.Cancellation);
            }
            else
            {
                _active++;
                queue.Active++;
                wait.Completion.TrySetResult(new RequestLease(this, queue));
            }
            RemoveEmpty(queue);
        }
    }

    private void RemoveEmpty(RequestQueue queue)
    {
        if (queue.Waiters.Count != 0) { return; }
        if (queue.Turn != null) { _turns.Remove(queue.Turn); queue.Turn = null; }
        if (queue.Active == 0) { _runs.Remove(queue.OperationId); }
    }

    private void Release(RequestQueue queue)
    {
        lock (_sync)
        {
            _active--;
            queue.Active--;
            RemoveEmpty(queue);
            Grant();
        }
    }

    private sealed class RequestLease(RequestBudget owner, RequestQueue queue) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) { owner.Release(queue); }
        }
    }
}
