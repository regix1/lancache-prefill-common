using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

#nullable enable

namespace LancachePrefill.Common;

public sealed class OwnedOperationCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, OperationRegistration> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationRegistration> _recent = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private Task? _disposal;
    private OwnedOperationResult _lastResult = OwnedOperationResult.Idle;
    private bool _disposed;

    public OwnedOperationCoordinator(int maxConcurrentRuns = PrefillProtocol.DefaultMaxRuns,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentRuns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrentRuns, 16);
        MaxConcurrentRuns = maxConcurrentRuns;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public int MaxConcurrentRuns { get; }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _active.Count != 0;
            }
        }
    }

    public OwnedOperationResult LastResult
    {
        get
        {
            lock (_sync)
            {
                return _active.Count == 0
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
            if (_active.Count != 0)
            {
                throw new InvalidOperationException("An operation is already running.");
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            registration = new OperationRegistration(cancellation)
            {
                Id = Guid.NewGuid().ToString("D"),
                Exclusive = true
            };
            _active.Add(registration.Id, registration);
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
            completion = SingleOperation()?.Completion.Task;
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
            registration = SingleOperation();
        }

        registration?.RequestCancellation();
        return registration == null
            ? Task.FromResult(OwnedOperationResult.Idle)
            : registration.Completion.Task.WaitAsync(cancellationToken);
    }

    public Task<OwnedOperationAdmission> StartAsync(string operationId, string fingerprint,
        RunProgress progress, Func<CancellationToken, Task> operation,
        CancellationToken lifetimeToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(operation);
        lifetimeToken.ThrowIfCancellationRequested();
        if (progress.Snapshot.OperationId != operationId)
        {
            throw new ArgumentException("Progress belongs to another operation.", nameof(progress));
        }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Prune();
            if (_active.TryGetValue(operationId, out var existing)
                || _recent.TryGetValue(operationId, out existing))
            {
                return Task.FromResult(existing.Fingerprint == fingerprint
                    ? new OwnedOperationAdmission(true, true, null, existing.Progress!.Snapshot)
                    : new OwnedOperationAdmission(false, false, "operation-conflict", null));
            }
            if (_active.Count >= MaxConcurrentRuns || _active.Values.Any(run => run.Exclusive))
            {
                return Task.FromResult(new OwnedOperationAdmission(false, false, "run-limit", null));
            }
            var registration = new OperationRegistration(
                CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken))
            {
                Id = operationId,
                Fingerprint = fingerprint,
                Progress = progress
            };
            _active.Add(operationId, registration);
            registration.Runner = Task.Run(() => RunOperationAsync(registration, operation), CancellationToken.None);
            return Task.FromResult(new OwnedOperationAdmission(true, false, null, progress.Snapshot));
        }
    }

    public RunSnapshot? GetOperation(string operationId)
    {
        lock (_sync) { return Lookup(operationId)?.Progress?.Snapshot; }
    }

    public OperationPage? GetOperation(string operationId, int offset, int limit)
    {
        lock (_sync) { return Lookup(operationId)?.Progress?.GetPage(offset, limit); }
    }

    public IReadOnlyList<RunSnapshot> GetActiveOperations()
    {
        lock (_sync)
        {
            return Array.AsReadOnly(_active.Values.Where(run => run.Progress != null)
                .Select(run => run.Progress!.Snapshot).OrderBy(run => run.StartedAt)
                .ThenBy(run => run.OperationId, StringComparer.Ordinal).ToArray());
        }
    }

    public IReadOnlyList<RunSnapshot> GetRecentOperations()
    {
        lock (_sync)
        {
            Prune();
            return Array.AsReadOnly(_recent.Values.Select(run => run.Progress!.Snapshot)
                .OrderBy(run => run.StartedAt).ThenBy(run => run.OperationId, StringComparer.Ordinal).ToArray());
        }
    }

    public RunSnapshot? Cancel(string operationId, string daemonInstanceId)
    {
        OperationRegistration? registration;
        lock (_sync)
        {
            registration = Lookup(operationId);
            if (registration?.Progress == null) { return null; }
            if (registration.Progress.Snapshot.DaemonInstanceId != daemonInstanceId)
            {
                throw new InvalidOperationException("instance-changed");
            }
            if (!_active.ContainsKey(operationId)) { return registration.Progress.Snapshot; }
            registration.Progress.TryChooseTerminal("cancelled");
            registration.Progress.MarkCancelling();
        }
        registration.RequestCancellation();
        return registration.Progress.Snapshot;
    }

    public Task<OwnedOperationResult> WaitAsync(string operationId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var run = Lookup(operationId) ?? throw new KeyNotFoundException("operation-not-found");
            return run.Completion.Task.WaitAsync(cancellationToken);
        }
    }

    public Task<OwnedOperationResult[]> CancelAllAndWaitAsync(CancellationToken cancellationToken = default)
    {
        OperationRegistration[] runs;
        lock (_sync) { runs = _active.Values.ToArray(); }
        foreach (var run in runs)
        {
            run.Progress?.TryChooseTerminal("cancelled");
            run.Progress?.MarkCancelling();
            run.RequestCancellation();
        }
        return Task.WhenAll(runs.Select(run => run.Completion.Task)).WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            _disposal ??= CancelAllAndWaitAsync();
            return new ValueTask(_disposal);
        }
    }

    private OperationRegistration? SingleOperation()
    {
        if (_active.Count > 1) { throw new InvalidOperationException("ambiguous-operation"); }
        return _active.Values.SingleOrDefault();
    }

    private OperationRegistration? Lookup(string operationId)
    {
        Prune();
        return _active.GetValueOrDefault(operationId) ?? _recent.GetValueOrDefault(operationId);
    }

    private void Prune()
    {
        var itemCount = _recent.Values.Sum(run => run.Progress!.ItemCount);
        foreach (var run in _recent.Values.OrderBy(run => run.CompletedAt).ToArray())
        {
            if (run.CompletedAt > _utcNow().AddHours(-PrefillProtocol.RetentionHours)
                && _recent.Count <= PrefillProtocol.RetentionOperations
                && itemCount <= PrefillProtocol.RetentionItems) { break; }
            itemCount -= run.Progress!.ItemCount;
            _recent.Remove(run.Id);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Process-owned tasks report failures through their retained completion result.")]
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

        var cancellationFailure = await registration.DisposeCancellationAsync().ConfigureAwait(false);
        if (cancellationFailure != null) { result = result with { Exception = cancellationFailure }; }

        if (registration.Progress != null)
        {
            if (result.Status != OwnedOperationStatus.Completed)
            {
                registration.Progress.TryChooseTerminal(
                    result.Status == OwnedOperationStatus.Cancelled ? "cancelled" : "failed");
            }
            await registration.Progress.CompleteAsync().ConfigureAwait(false);
            result = new OwnedOperationResult(registration.Progress.Snapshot.State switch
            {
                "cancelled" => OwnedOperationStatus.Cancelled,
                "failed" => OwnedOperationStatus.Failed,
                _ => OwnedOperationStatus.Completed
            }, result.Exception);
        }

        lock (_sync)
        {
            _active.Remove(registration.Id);
            _lastResult = result;
            if (registration.Progress != null)
            {
                registration.CompletedAt = _utcNow();
                _recent.Add(registration.Id, registration);
                Prune();
            }
            registration.Completion.TrySetResult(result);
        }
    }

    private sealed class OperationRegistration
    {
        private readonly object _cancellationSync = new();
        private Task _cancellationTask = Task.CompletedTask;
        private bool _cancellationStarted;
        private bool _cancellationClosed;

        public OperationRegistration(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
            Completion = new TaskCompletionSource<OwnedOperationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public required string Id { get; init; }
        public string? Fingerprint { get; init; }
        public bool Exclusive { get; init; }
        public RunProgress? Progress { get; init; }
        public DateTimeOffset CompletedAt { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<OwnedOperationResult> Completion { get; }
        public Task? Runner { get; set; }

        public void RequestCancellation()
        {
            lock (_cancellationSync)
            {
                if (_cancellationClosed || _cancellationStarted) { return; }
                _cancellationStarted = true;
                _cancellationTask = Cancellation.CancelAsync();
            }
        }

        public async Task<Exception?> DisposeCancellationAsync()
        {
            Task cancellation;
            lock (_cancellationSync)
            {
                _cancellationClosed = true;
                cancellation = _cancellationTask;
            }
            try
            {
                await cancellation.ConfigureAwait(false);
                return null;
            }
            catch (AggregateException exception)
            {
                return exception;
            }
            finally { Cancellation.Dispose(); }
        }
    }
}
