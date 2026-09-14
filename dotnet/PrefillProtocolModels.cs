using System.Collections.Generic;

#nullable enable

namespace LancachePrefill.Common;

public sealed record RunOptions
{
    public IReadOnlyList<string>? AppIds { get; init; }
    public string Selection { get; init; } = "selected";
    public bool Force { get; init; }
    public IReadOnlyList<string> OperatingSystems { get; init; } = Array.Empty<string>();
    public int MaxConcurrency { get; init; }
    public int? TopCount { get; init; }
    public IReadOnlyList<string> CachedDepots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CachedAppInput> CachedApps { get; init; } = Array.Empty<CachedAppInput>();
}

public sealed record CachedAppInput
{
    public required string AppId { get; init; }
    public string? Revision { get; init; }
}

public sealed record OperationPage(
    RunSnapshot Operation,
    RunOptions Options,
    bool SelectionResolved,
    int TotalItems,
    IReadOnlyList<RunItemSnapshot> Items,
    int? NextOffset);
