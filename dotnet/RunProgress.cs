using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

#nullable enable

namespace LancachePrefill.Common;

public sealed class RunProgress
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RunItemSnapshot> _items = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly RunOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _sendTimeout;
    private Func<RunSnapshot, CancellationToken, Task>? _publish;
    private RunSnapshot _snapshot;
    private RunSnapshot? _pending;
    private RunTerminal? _terminal;
    private bool _completed;
    private Task _sending = Task.CompletedTask;

    public RunProgress(string operationId, string daemonInstanceId, RunOptions options,
        Func<RunSnapshot, CancellationToken, Task>? publish = null,
        Func<DateTimeOffset>? utcNow = null, TimeSpan? sendTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(daemonInstanceId);
        ArgumentNullException.ThrowIfNull(options);
        _options = options with
        {
            AppIds = options.AppIds == null ? null : PrefillProtocol.NormalizeIds(options.AppIds),
            OperatingSystems = PrefillProtocol.NormalizeIds(options.OperatingSystems),
            CachedDepots = PrefillProtocol.NormalizeIds(options.CachedDepots)
        };
        _publish = publish;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _sendTimeout = sendTimeout ?? TimeSpan.FromSeconds(5);
        ArgumentOutOfRangeException.ThrowIfLessThan(_sendTimeout, TimeSpan.Zero);
        var now = _utcNow();
        _snapshot = new RunSnapshot
        {
            OperationId = operationId,
            DaemonInstanceId = daemonInstanceId,
            StartedAt = now,
            UpdatedAt = now
        };
        if (_options.AppIds != null)
        {
            foreach (var id in _options.AppIds)
            {
                _order.Add(id);
                _items.Add(id, new RunItemSnapshot { AppId = id });
            }
            _snapshot = _snapshot with { SelectionResolved = true, TotalApps = _order.Count };
        }
    }

    public RunSnapshot Snapshot { get { lock (_sync) { return _snapshot; } } }
    public RunTerminal? Terminal { get { lock (_sync) { return _terminal; } } }
    public int ItemCount { get { lock (_sync) { return _items.Count; } } }

    public void ResolveSelection(IEnumerable<string> appIds)
    {
        var ids = PrefillProtocol.NormalizeIds(appIds);
        lock (_sync)
        {
            if (_terminal != null) { return; }
            if (_snapshot.SelectionResolved)
            {
                throw new InvalidOperationException("Selection has already been resolved.");
            }
            foreach (var id in ids)
            {
                _order.Add(id);
                _items.Add(id, new RunItemSnapshot { AppId = id });
            }
            _snapshot = _snapshot with { SelectionResolved = true, TotalApps = ids.Count };
            Advance();
        }
    }

    public bool UpdateItem(RunItemSnapshot item)
        => UpdateItem(item, null);

    public bool TryCommitItem(RunItemSnapshot item, Action commit)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(commit);
        if (item.Result is not ("success" or "already_cached"))
        {
            throw new ArgumentException("An item commit requires a successful result.", nameof(item));
        }
        return UpdateItem(item, commit);
    }

    private bool UpdateItem(RunItemSnapshot item, Action? commit)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            if (_terminal != null || (commit != null && _snapshot.State == "cancelling")) { return false; }
            if (!_items.TryGetValue(item.AppId, out var previous))
            {
                throw new ArgumentException("The item is not in the resolved selection.", nameof(item));
            }
            if (previous.Result != null) { return false; }
            if (item.BytesTransferred < previous.BytesTransferred)
            {
                throw new ArgumentException("Transferred bytes cannot decrease.", nameof(item));
            }
            item = item with
            {
                Sequence = _snapshot.Sequence + 1,
                Name = Truncate(item.Name),
                Reason = Truncate(item.Reason)
            };
            // Prepare temporary content beforehand; only the final synchronous replacement belongs under this lock.
            commit?.Invoke();
            _items[item.AppId] = item;
            _snapshot = _snapshot with { CurrentItem = item, State = "downloading" };
            Advance();
            return true;
        }
    }

    public bool TryChooseTerminal(string state, string? reason = null)
    {
        if (state is not ("completed" or "failed" or "cancelled"))
        {
            throw new ArgumentException("Invalid terminal state.", nameof(state));
        }
        lock (_sync)
        {
            if (_terminal != null) { return false; }
            _terminal = new RunTerminal(state == "completed" && _items.Values.Any(item => item.Result == "failed")
                ? "failed" : state, Truncate(reason));
            return true;
        }
    }

    public void MarkCancelling()
    {
        lock (_sync)
        {
            if (_completed || _snapshot.State == "cancelling"
                || (_terminal != null && _terminal.State != "cancelled")) { return; }
            _snapshot = _snapshot with { State = "cancelling" };
            Advance();
        }
    }

    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "The process-owned publication task has no synchronization-context dependency.")]
    public Task CompleteAsync(long? finalBytesTransferred = null,
        IReadOnlyDictionary<string, long>? itemBytesTransferred = null)
    {
        lock (_sync)
        {
            if (_completed) { return _sending; }
            if (itemBytesTransferred != null)
            {
                foreach (var entry in itemBytesTransferred)
                {
                    if (!_items.TryGetValue(entry.Key, out var item) || entry.Value < item.BytesTransferred)
                    {
                        throw new ArgumentException("Final bytes must belong to a selected item and cannot decrease.", nameof(itemBytesTransferred));
                    }
                }
                foreach (var entry in itemBytesTransferred)
                {
                    _items[entry.Key] = _items[entry.Key] with
                    {
                        BytesTransferred = entry.Value,
                        Sequence = _snapshot.Sequence + 1
                    };
                }
            }
            var failed = _items.Values.Any(item => item.Result == "failed");
            _terminal ??= new RunTerminal(failed ? "failed" : "completed", null);
            foreach (var id in _order)
            {
                var item = _items[id];
                if (item.Result == null)
                {
                    _items[id] = item with
                    {
                        State = _terminal.State == "cancelled" ? "cancelled" : "skipped",
                        Result = _terminal.State == "cancelled" ? "cancelled" : "skipped",
                        Reason = _terminal.Reason ?? "notAttempted",
                        Sequence = _snapshot.Sequence + 1
                    };
                }
            }
            var allOverlap = _items.Count > 0 && _items.Values.All(item =>
                item.Result == "skipped" && item.Reason == "skippedOverlap");
            _snapshot = _snapshot with
            {
                State = _terminal.State,
                Reason = _terminal.Reason ?? (allOverlap ? "skippedOverlap" : null),
                CurrentItem = _snapshot.CurrentItem == null ? null : _items[_snapshot.CurrentItem.AppId]
            };
            _completed = true;
            Advance(finalBytesTransferred);
            return _sending;
        }
    }

    public OperationPage GetPage(int offset = 0, int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, _order.Count);
            var page = _order.Skip(offset).Take(limit).Select(id => _items[id]).ToArray();
            var next = offset + page.Length;
            var options = _options with { AppIds = null, CachedDepots = Array.Empty<string>() };
            return new OperationPage(_snapshot, options, _snapshot.SelectionResolved,
                _order.Count, Array.AsReadOnly(page), next < _order.Count ? next : null);
        }
    }

    private void Advance(in long? finalBytesTransferred = null)
    {
        _snapshot = _snapshot with
        {
            Sequence = _snapshot.Sequence + 1,
            UpdatedAt = _utcNow(),
            CompletedApps = _items.Values.Count(item => item.Result == "success"),
            CachedApps = _items.Values.Count(item => item.Result == "already_cached"),
            FailedApps = _items.Values.Count(item => item.Result == "failed"),
            CancelledApps = _items.Values.Count(item => item.Result == "cancelled"),
            SkippedApps = _items.Values.Count(item => item.Result == "skipped"),
            BytesTransferred = Math.Max(finalBytesTransferred ?? 0, _items.Values.Sum(item => item.BytesTransferred))
        };
        if (_publish == null) { return; }
        _pending = _snapshot;
        if (_sending.IsCompleted) { _sending = Task.Run(SendAsync); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A subscriber failure detaches that subscriber; retained snapshots remain authoritative.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The cancellation continuation disposes its source after subscriber callbacks have finished.")]
    private async Task SendAsync()
    {
        while (true)
        {
            RunSnapshot next;
            Func<RunSnapshot, CancellationToken, Task> publish;
            lock (_sync)
            {
                if (_pending == null || _publish == null)
                {
                    _sending = Task.CompletedTask;
                    return;
                }
                next = _pending;
                _pending = null;
                publish = _publish;
            }
            var timeout = new CancellationTokenSource();
            try
            {
                var send = publish(next, timeout.Token);
                try { await send.WaitAsync(_sendTimeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    _ = send.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    throw;
                }
            }
            catch (Exception)
            {
                // Recovery reads retain transitions even when the transport no longer accepts them.
                lock (_sync) { _publish = null; _pending = null; }
            }
            finally
            {
                _ = timeout.CancelAsync().ContinueWith(task =>
                {
                    _ = task.Exception;
                    timeout.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static string? Truncate(string? value) => value?.Length > 1024 ? value[..1024] : value;
}
