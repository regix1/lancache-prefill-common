using System.Collections.Generic;
using System.Threading;

#nullable enable

namespace LancachePrefill.Common;

internal sealed class RequestQueue
{
    public required string OperationId { get; init; }
    public required int Limit { get; init; }
    public int Active { get; set; }
    public LinkedList<RequestWait> Waiters { get; } = new();
    public LinkedListNode<RequestQueue>? Turn { get; set; }
}

internal sealed class RequestWait
{
    public required RequestQueue Queue { get; init; }
    public required CancellationToken Cancellation { get; init; }
    public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public LinkedListNode<RequestWait>? Node { get; set; }
}
