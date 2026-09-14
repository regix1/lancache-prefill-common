using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#nullable enable

namespace LancachePrefill.Common;

public sealed class PrefillProtocol
{
    public const int Version = 2;
    public const int DefaultMaxRuns = 4;
    public const int RetentionHours = 24;
    public const int RetentionOperations = 256;
    public const int RetentionItems = 10000;
    public static IReadOnlyList<string> Features { get; } = Array.AsReadOnly(new[]
    {
        "concurrentPrefill", "operationProgress", "targetedCancel", "inlineSelection", "activeOperations"
    });

    public string DaemonInstanceId { get; } = Guid.NewGuid().ToString("D");
    public int MaxConcurrentRuns { get; }
    public int MaxConcurrentRequests { get; }

    public PrefillProtocol(int defaultMaxRequests, string? maxRuns = null, string? maxRequests = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultMaxRequests, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultMaxRequests, 128);
        MaxConcurrentRuns = ParseLimit(maxRuns, DefaultMaxRuns, 16, "PREFILL_MAX_RUNS");
        MaxConcurrentRequests = ParseLimit(maxRequests, defaultMaxRequests, 128, "PREFILL_MAX_REQUESTS");
    }

    public static PrefillProtocol FromEnvironment(int defaultMaxRequests, int? requestOverride = null)
    {
        var requests = requestOverride?.ToString(CultureInfo.InvariantCulture)
            ?? Environment.GetEnvironmentVariable("PREFILL_MAX_REQUESTS");
        return new PrefillProtocol(defaultMaxRequests,
            Environment.GetEnvironmentVariable("PREFILL_MAX_RUNS"), requests);
    }

    public void ValidateInstance(string daemonInstanceId)
    {
        if (!StringComparer.Ordinal.Equals(DaemonInstanceId, daemonInstanceId))
        {
            throw new InvalidOperationException("instance-changed");
        }
    }

    public RunOptions Capture(RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "maxConcurrency must be positive.");
        }
        if (options.AppIds != null && options.Selection != "selected")
        {
            throw new ArgumentException("Inline appIds conflict with a selection preset.", nameof(options));
        }
        if (options.Selection == "selected" && (options.AppIds == null || options.AppIds.Count == 0))
        {
            throw new ArgumentException("Explicit selection is empty.", nameof(options));
        }
        return options with
        {
            AppIds = options.AppIds == null ? null : NormalizeIds(options.AppIds),
            OperatingSystems = NormalizeIds(options.OperatingSystems),
            CachedDepots = NormalizeIds(options.CachedDepots),
            CachedApps = NormalizeCachedApps(options.CachedApps),
            MaxConcurrency = Math.Min(options.MaxConcurrency, MaxConcurrentRequests)
        };
    }

    public static IReadOnlyList<string> NormalizeIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (id.Length > 1024) { throw new ArgumentException("Item identifiers must not exceed 1024 characters.", nameof(ids)); }
            if (seen.Add(id))
            {
                result.Add(id);
            }
        }
        return result.AsReadOnly();
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<CachedAppInput> NormalizeCachedApps(
        IEnumerable<CachedAppInput> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CachedAppInput>();
        foreach (var app in apps)
        {
            ArgumentNullException.ThrowIfNull(app);
            ArgumentException.ThrowIfNullOrWhiteSpace(app.AppId);
            if (app.AppId.Length > 1024)
            {
                throw new ArgumentException("Item identifiers must not exceed 1024 characters.", nameof(apps));
            }
            if (seen.Add(app.AppId))
            {
                result.Add(new CachedAppInput { AppId = app.AppId, Revision = app.Revision });
            }
        }
        return result.AsReadOnly();
    }

    public static string Fingerprint(RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = new StringBuilder();
        void Append(string value) => text.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value);
        void AppendIds(IReadOnlyList<string>? ids)
        {
            Append(ids == null ? "absent" : ids.Count.ToString(CultureInfo.InvariantCulture));
            if (ids != null)
            {
                foreach (var id in ids) { Append(id); }
            }
        }
        Append(options.Selection);
        Append(options.Force ? "true" : "false");
        Append(options.MaxConcurrency.ToString(CultureInfo.InvariantCulture));
        Append(options.TopCount?.ToString(CultureInfo.InvariantCulture) ?? "absent");
        AppendIds(options.AppIds);
        AppendIds(options.OperatingSystems);
        AppendIds(options.CachedDepots);
        Append(options.CachedApps.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var app in options.CachedApps)
        {
            Append(app.AppId);
            Append(app.Revision ?? "absent");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static int ParseLimit(string? value, int fallback, int maximum, string setting)
    {
        if (value == null) { return fallback; }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit < 1 || limit > maximum)
        {
            throw new ArgumentException($"{setting} must be an integer from 1 through {maximum}.", setting);
        }
        return limit;
    }
}
