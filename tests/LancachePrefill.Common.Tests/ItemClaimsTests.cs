namespace LancachePrefill.Common.Tests;

public sealed class ItemClaimsTests
{
    [Fact]
    public void Claim_IsAtomicAcrossSharedKeysAndReleasesExactlyOnce()
    {
        var claims = new ItemClaims();
        var first = claims.TryClaim("a", ["app:1", "depot:2", "depot:2"]);
        Assert.NotNull(first);
        Assert.Null(claims.TryClaim("b", ["app:3", "depot:2"]));
        using var disjoint = claims.TryClaim("c", ["app:3", "depot:4"]);
        Assert.NotNull(disjoint);
        first.Dispose();
        using var successor = claims.TryClaim("b", ["app:1", "depot:2"]);
        Assert.NotNull(successor);
        first.Dispose();
        Assert.Null(claims.TryClaim("d", ["depot:2"]));
    }

    [Fact]
    public async Task Claim_ConcurrentContendersHaveOneOwner()
    {
        var claims = new ItemClaims();
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => Task.Run(() => claims.TryClaim(index.ToString(), ["product"]))));
        Assert.Single(results, result => result != null);
        foreach (var result in results) { result?.Dispose(); }
        using var after = claims.TryClaim("after", ["product"]);
        Assert.NotNull(after);
    }
}
