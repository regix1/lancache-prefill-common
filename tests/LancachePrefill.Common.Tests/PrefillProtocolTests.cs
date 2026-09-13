namespace LancachePrefill.Common.Tests;

public sealed class PrefillProtocolTests
{
    [Theory]
    [InlineData(null, 4)]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    [InlineData("16", 16)]
    public void Limits_AreCapturedAtConstruction(string? value, int expected)
    {
        var protocol = new PrefillProtocol(30, value);
        Assert.Equal(expected, protocol.MaxConcurrentRuns);
        Assert.Equal(30, protocol.MaxConcurrentRequests);
        Assert.NotEqual(protocol.DaemonInstanceId, new PrefillProtocol(30, value).DaemonInstanceId);
        Assert.Throws<InvalidOperationException>(() => protocol.ValidateInstance(Guid.NewGuid().ToString()));
        protocol.ValidateInstance(protocol.DaemonInstanceId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("two")]
    public void RunLimit_RejectsInvalidExplicitValues(string value)
        => Assert.Throws<ArgumentException>(() => new PrefillProtocol(30, value));

    [Theory]
    [InlineData("0")]
    [InlineData("129")]
    [InlineData("invalid")]
    public void RequestLimit_RejectsInvalidExplicitValues(string value)
        => Assert.Throws<ArgumentException>(() => new PrefillProtocol(30, maxRequests: value));

    [Fact]
    public void Capture_CopiesAndDeduplicatesWithoutChangingSelectionOrder()
    {
        var ids = new List<string> { "a", "b", "a" };
        var systems = new List<string> { "linux" };
        var protocol = new PrefillProtocol(3);
        var options = protocol.Capture(new RunOptions
        {
            AppIds = ids,
            OperatingSystems = systems,
            MaxConcurrency = 30,
            Force = true
        });
        var fingerprint = PrefillProtocol.Fingerprint(options);
        ids[0] = "changed";
        systems.Clear();
        Assert.Equal(new[] { "a", "b" }, options.AppIds);
        Assert.Equal(new[] { "linux" }, options.OperatingSystems);
        Assert.Equal(3, options.MaxConcurrency);
        Assert.Equal(fingerprint, PrefillProtocol.Fingerprint(options));
        Assert.NotEqual(fingerprint, PrefillProtocol.Fingerprint(options with { Force = false }));
        Assert.Throws<ArgumentException>(() => protocol.Capture(new RunOptions { AppIds = [], MaxConcurrency = 1 }));
        Assert.Throws<ArgumentException>(() => protocol.Capture(options with { Selection = "all" }));
        Assert.Null(protocol.Capture(new RunOptions { Selection = "all", MaxConcurrency = 1 }).AppIds);
    }

    [Fact]
    public void RequestCeiling_IsIndependentOfRunCapacity()
    {
        Assert.Equal(25, new PrefillProtocol(25, "16").MaxConcurrentRequests);
        Assert.Equal(128, new PrefillProtocol(20, "1", "128").MaxConcurrentRequests);
        Assert.Equal(5, PrefillProtocol.Features.Count);
    }

    [Fact]
    public void EnvironmentChanges_ApplyOnlyToANewInstance()
    {
        var previousRuns = Environment.GetEnvironmentVariable("PREFILL_MAX_RUNS");
        var previousRequests = Environment.GetEnvironmentVariable("PREFILL_MAX_REQUESTS");
        try
        {
            Environment.SetEnvironmentVariable("PREFILL_MAX_RUNS", "3");
            Environment.SetEnvironmentVariable("PREFILL_MAX_REQUESTS", "7");
            var first = PrefillProtocol.FromEnvironment(30);
            Environment.SetEnvironmentVariable("PREFILL_MAX_RUNS", "16");
            Environment.SetEnvironmentVariable("PREFILL_MAX_REQUESTS", "8");
            Assert.Equal(3, first.MaxConcurrentRuns);
            Assert.Equal(7, first.MaxConcurrentRequests);
            var second = PrefillProtocol.FromEnvironment(30, 5);
            Assert.Equal(16, second.MaxConcurrentRuns);
            Assert.Equal(5, second.MaxConcurrentRequests);
            Assert.NotEqual(first.DaemonInstanceId, second.DaemonInstanceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PREFILL_MAX_RUNS", previousRuns);
            Environment.SetEnvironmentVariable("PREFILL_MAX_REQUESTS", previousRequests);
        }
    }
}
