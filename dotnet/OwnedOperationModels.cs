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

public sealed record OwnedOperationAdmission(bool Accepted, bool Replayed, string? Error, RunSnapshot? Operation);
