using System.Collections.Generic;

#nullable enable

namespace LancachePrefill.Common;

public sealed record RunItemSnapshot
{
    public required string AppId { get; init; }
    public string? Name { get; init; }
    public string State { get; init; } = "pending";
    public string? Result { get; init; }
    public string? Reason { get; init; }
    public long Sequence { get; init; }
    public long BytesTransferred { get; init; }
    public long? TotalBytes { get; init; }
    public string? CacheRevision { get; init; }
}

public sealed record RunSnapshot
{
    public required string OperationId { get; init; }
    public required string DaemonInstanceId { get; init; }
    public long Sequence { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string State { get; init; } = "preparing";
    public string? Reason { get; init; }
    public bool SelectionResolved { get; init; }
    public RunItemSnapshot? CurrentItem { get; init; }
    public int TotalApps { get; init; }
    public int CompletedApps { get; init; }
    public int CachedApps { get; init; }
    public int FailedApps { get; init; }
    public int CancelledApps { get; init; }
    public int SkippedApps { get; init; }
    public long BytesTransferred { get; init; }
}

public sealed record RunTerminal(string State, string? Reason);
