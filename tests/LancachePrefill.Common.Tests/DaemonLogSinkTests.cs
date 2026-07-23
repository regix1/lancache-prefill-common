namespace LancachePrefill.Common.Tests;

public sealed class DaemonLogSinkTests
{
    [Fact]
    public void DefaultSink_SuppressesDebugAndRetainsOperationalLevels()
    {
        var messages = new List<string>();
        var sink = new DaemonLogSink(messages.Add);

        sink.Write(DaemonLogLevel.Debug, "debug");
        sink.Write(DaemonLogLevel.Info, "info");
        sink.Write(DaemonLogLevel.Warning, "warning");
        sink.Write(DaemonLogLevel.Error, "error");

        Assert.Equal(new[] { "info", "warning", "error" }, messages);
    }

    [Fact]
    public void DebugMinimum_ExplicitlyEnablesDebug()
    {
        var messages = new List<string>();
        var sink = new DaemonLogSink(messages.Add, DaemonLogLevel.Debug);

        sink.Write(DaemonLogLevel.Debug, "debug");

        Assert.Equal(new[] { "debug" }, messages);
    }
}
