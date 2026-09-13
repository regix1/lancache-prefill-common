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
}

public sealed record OperationPage(
    RunSnapshot Operation,
    RunOptions Options,
    bool SelectionResolved,
    int TotalItems,
    IReadOnlyList<RunItemSnapshot> Items,
    int? NextOffset);
