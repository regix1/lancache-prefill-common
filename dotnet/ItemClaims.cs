using System.Collections.Generic;
using System.Threading;

#nullable enable

namespace LancachePrefill.Common;

public sealed class ItemClaims
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Guid> _claims = new(StringComparer.Ordinal);

    public IDisposable? TryClaim(string operationId, IEnumerable<string> keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var names = PrefillProtocol.NormalizeIds(keys).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) { throw new ArgumentException("At least one item key is required.", nameof(keys)); }
        lock (_sync)
        {
            if (names.Any(_claims.ContainsKey)) { return null; }
            var claim = Guid.NewGuid();
            foreach (var name in names) { _claims.Add(name, claim); }
            return new ItemLease(this, names, claim);
        }
    }

    private void Release(string[] keys, Guid claim)
    {
        lock (_sync)
        {
            foreach (var key in keys)
            {
                if (_claims.TryGetValue(key, out var owner) && owner == claim) { _claims.Remove(key); }
            }
        }
    }

    private sealed class ItemLease(ItemClaims owner, string[] keys, Guid claim) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) { owner.Release(keys, claim); }
        }
    }
}
