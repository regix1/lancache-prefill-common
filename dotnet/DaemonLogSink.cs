#nullable enable

namespace LancachePrefill.Common;

public enum DaemonLogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public sealed class DaemonLogSink
{
    private readonly Action<string> _writeLine;

    public DaemonLogSink(
        Action<string> writeLine,
        DaemonLogLevel minimumLevel = DaemonLogLevel.Info)
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        if (!Enum.IsDefined(minimumLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLevel));
        }

        _writeLine = writeLine;
        MinimumLevel = minimumLevel;
    }

    public DaemonLogLevel MinimumLevel { get; }

    public bool IsEnabled(DaemonLogLevel level)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        return level >= MinimumLevel;
    }

    public void Write(DaemonLogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (IsEnabled(level))
        {
            _writeLine(message);
        }
    }
}
