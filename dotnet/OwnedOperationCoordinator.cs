using System.Threading;

#nullable enable

namespace LancachePrefill.Common;

public enum OwnedOperationStatus
{
    Idle,
    Running,
    Completed,
    Cancelled,
    Failed
}

public sealed record OwnedOperationResult(OwnedOperationStatus Status, Exception? Exception = null)
{
    public static OwnedOperationResult Idle { get; } = new(OwnedOperationStatus.Idle);
}

public sealed class OwnedOperationCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private OperationRegistration? _current;
    private OwnedOperationResult _lastResult = OwnedOperationResult.Idle;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _current != null;
            }
        }
    }

    public OwnedOperationResult LastResult
    {
        get
        {
            lock (_sync)
            {
                return _current == null
                    ? _lastResult
                    : new OwnedOperationResult(OwnedOperationStatus.Running);
            }
        }
    }

    public Task StartAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken lifetimeToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lifetimeToken.ThrowIfCancellationRequested();

        OperationRegistration registration;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current != null)
            {
                throw new InvalidOperationException("An operation is already running.");
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            registration = new OperationRegistration(cancellation);
            _current = registration;
            _lastResult = new OwnedOperationResult(OwnedOperationStatus.Running);
            registration.Runner = Task.Run(
                () => RunOperationAsync(registration, operation),
                CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task<OwnedOperationResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        Task<OwnedOperationResult>? completion;
        OwnedOperationResult completedResult;
        lock (_sync)
        {
            completion = _current?.Completion.Task;
            completedResult = _lastResult;
        }

        return completion == null
            ? Task.FromResult(completedResult)
            : completion.WaitAsync(cancellationToken);
    }

    public Task<OwnedOperationResult> CancelAndWaitAsync(CancellationToken cancellationToken = default)
    {
        OperationRegistration? registration;
        lock (_sync)
        {
            registration = _current;
        }

        registration?.RequestCancellation();
        return registration == null
            ? Task.FromResult(OwnedOperationResult.Idle)
            : registration.Completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        OperationRegistration? registration;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registration = _current;
        }

        if (registration != null)
        {
            registration.RequestCancellation();
            await registration.Completion.Task.ConfigureAwait(false);
        }
    }

    private async Task RunOperationAsync(
        OperationRegistration registration,
        Func<CancellationToken, Task> operation)
    {
        OwnedOperationResult result;
        try
        {
            await operation(registration.Cancellation.Token).ConfigureAwait(false);
            result = registration.Cancellation.IsCancellationRequested
                ? new OwnedOperationResult(OwnedOperationStatus.Cancelled)
                : new OwnedOperationResult(OwnedOperationStatus.Completed);
        }
        catch (OperationCanceledException) when (registration.Cancellation.IsCancellationRequested)
        {
            result = new OwnedOperationResult(OwnedOperationStatus.Cancelled);
        }
        catch (Exception exception)
        {
            result = new OwnedOperationResult(OwnedOperationStatus.Failed, exception);
        }

        lock (_sync)
        {
            if (ReferenceEquals(_current, registration))
            {
                _current = null;
                _lastResult = result;
            }
        }

        registration.DisposeCancellation();
        registration.Completion.TrySetResult(result);
    }

    private sealed class OperationRegistration
    {
        private readonly object _cancellationSync = new();
        private int _activeCancellationRequests;
        private bool _disposeCancellationRequested;
        private bool _cancellationDisposed;

        public OperationRegistration(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
            Completion = new TaskCompletionSource<OwnedOperationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public CancellationTokenSource Cancellation { get; }

        public TaskCompletionSource<OwnedOperationResult> Completion { get; }

        public Task? Runner { get; set; }

        public void RequestCancellation()
        {
            lock (_cancellationSync)
            {
                if (_cancellationDisposed)
                {
                    return;
                }

                _activeCancellationRequests++;
            }

            try
            {
                Cancellation.Cancel();
            }
            finally
            {
                var dispose = false;
                lock (_cancellationSync)
                {
                    _activeCancellationRequests--;
                    if (_activeCancellationRequests == 0 && _disposeCancellationRequested)
                    {
                        _cancellationDisposed = true;
                        dispose = true;
                    }
                }

                if (dispose)
                {
                    Cancellation.Dispose();
                }
            }
        }

        public void DisposeCancellation()
        {
            var dispose = false;
            lock (_cancellationSync)
            {
                if (_cancellationDisposed)
                {
                    return;
                }

                if (_activeCancellationRequests == 0)
                {
                    _cancellationDisposed = true;
                    dispose = true;
                }
                else
                {
                    _disposeCancellationRequested = true;
                }
            }

            if (dispose)
            {
                Cancellation.Dispose();
            }
        }
    }
}
