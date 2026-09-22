using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RepoContext.Core.Stats;

namespace RepoContext.Core.Guard;

/// <summary>Bounded local counters for the read-cost guard.</summary>
/// <remarks>
/// Counts only. No path, no command text, no prompt, no session id and no
/// source ever enters this file: a guard ledger that recorded what an agent read
/// would be a new place for sensitive repository paths to leak from, which is
/// precisely what the read-cost policy exists to avoid needing.
/// </remarks>
public sealed record GuardCounters
{
    /// <summary>Tool calls the guard inspected.</summary>
    public int Observed { get; init; }

    /// <summary>Reads judged cheap enough.</summary>
    public int Allowed { get; init; }

    /// <summary>Expensive reads: denied in enforce mode, only counted in observe mode.</summary>
    public int Redirected { get; init; }

    /// <summary>Expensive reads allowed through because the redirect bound was reached.</summary>
    public int Escalated { get; init; }

    /// <summary>Calls outside the supported subset, handled normally by the client.</summary>
    public int Unsupported { get; init; }

    /// <summary>Evaluations that failed and therefore allowed the read.</summary>
    public int Error { get; init; }

    /// <summary>Denials actually emitted to the client (a subset of <see cref="Redirected"/>).</summary>
    public int Denied { get; init; }

    /// <summary>Latency samples, in fixed buckets.</summary>
    public GuardLatency Latency { get; init; } = new();

    /// <summary>Adds one outcome.</summary>
    public GuardCounters With(GuardOutcome outcome, bool denied, long elapsedMilliseconds) => this with
    {
        Observed = Observed + 1,
        Allowed = Allowed + (outcome == GuardOutcome.Allowed ? 1 : 0),
        Redirected = Redirected + (outcome == GuardOutcome.Redirected ? 1 : 0),
        Escalated = Escalated + (outcome == GuardOutcome.Escalated ? 1 : 0),
        Unsupported = Unsupported + (outcome == GuardOutcome.Unsupported ? 1 : 0),
        Error = Error + (outcome == GuardOutcome.Error ? 1 : 0),
        Denied = Denied + (denied ? 1 : 0),
        Latency = Latency.With(elapsedMilliseconds),
    };
}

/// <summary>A bounded latency histogram: fixed buckets, no unbounded sample list.</summary>
public sealed record GuardLatency
{
    /// <summary>Upper bounds, in milliseconds. The last bucket is everything above.</summary>
    public static readonly int[] Bounds = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1_000];

    public int Count { get; init; }

    public long TotalMs { get; init; }

    public long MaxMs { get; init; }

    /// <summary>One counter per bucket in <see cref="Bounds"/>, plus an overflow bucket.</summary>
    public IReadOnlyList<int> Buckets { get; init; } = new int[Bounds.Length + 1];

    /// <summary>Records one sample.</summary>
    public GuardLatency With(long elapsedMilliseconds)
    {
        long sample = Math.Max(elapsedMilliseconds, 0);
        int[] buckets = Buckets.Count == Bounds.Length + 1
            ? [.. Buckets]
            : new int[Bounds.Length + 1];
        int index = Bounds.Length;
        for (int i = 0; i < Bounds.Length; i++)
        {
            if (sample <= Bounds[i])
            {
                index = i;
                break;
            }
        }

        buckets[index]++;
        return new GuardLatency
        {
            Count = Count + 1,
            TotalMs = TotalMs + sample,
            MaxMs = Math.Max(MaxMs, sample),
            Buckets = buckets,
        };
    }

    /// <summary>
    /// The bucket upper bound at or below which <paramref name="quantile"/> of
    /// samples fall. An upper bound, not an exact percentile: the histogram is
    /// bounded on purpose, so the figure is reported as "&lt;= n ms".
    /// </summary>
    public int? QuantileUpperBoundMs(double quantile)
    {
        if (Count == 0 || Buckets.Count != Bounds.Length + 1)
        {
            return null;
        }

        int target = (int)Math.Ceiling(quantile * Count);
        int seen = 0;
        for (int i = 0; i < Bounds.Length; i++)
        {
            seen += Buckets[i];
            if (seen >= target)
            {
                return Bounds[i];
            }
        }

        return null;
    }
}

/// <summary>
/// The guard's small local state: bounded counters and the per-epoch redirect
/// bookkeeping that keeps enforcement from looping.
/// </summary>
/// <remarks>
/// <para>
/// Two things live here for two different reasons. The counters are usage
/// statistics and honour <c>REPOCTX_NO_STATS</c> exactly like the response
/// ledger does. The redirect counts are <i>policy state</i>: without them an
/// enforcing guard could deny the same read forever, so they are kept even when
/// statistics are switched off. They carry no usage data — a truncated hash of a
/// path and a small integer — and are bounded in both dimensions.
/// </para>
/// <para>
/// Every failure here is non-fatal. A guard that cannot persist its state
/// degrades to allowing reads, never to blocking them.
/// </para>
/// </remarks>
public static class GuardState
{
    private const int StoreLockTimeoutMilliseconds = 0;
    private const int MaxTrackedEpochs = 8;
    private const int MaxTrackedPathsPerEpoch = 256;

    /// <summary>The guard state file inside the index directory.</summary>
    public const string FileName = "guard.json";

    /// <summary>The guard state path for a repository.</summary>
    public static string PathFor(RepoLayout layout) =>
        Path.Combine(layout.IndexDirectory, FileName);

    /// <summary>Reads the counters; an absent or damaged file reads as empty.</summary>
    public static GuardCounters ReadCounters(RepoLayout layout) =>
        Read(PathFor(layout)).Counters;

    /// <summary>How often <paramref name="path"/> was redirected in this epoch.</summary>
    public static int Redirects(RepoLayout layout, string epochKey, string path)
    {
        StateFile file = Read(PathFor(layout));
        return file.Redirects.TryGetValue(epochKey, out Dictionary<string, int>? paths)
               && paths.TryGetValue(PathKey(path), out int count)
            ? count
            : 0;
    }

    /// <summary>
    /// Records one decision: the counters when statistics are enabled, the
    /// redirect bookkeeping whenever a denial was actually emitted.
    /// </summary>
    public static bool Record(
        RepoLayout layout,
        string epochKey,
        GuardDecision decision,
        long elapsedMilliseconds)
    {
        bool countersWanted = UsageRecorder.Enabled;
        bool redirectsWanted = decision.Deny;
        if (!countersWanted && !redirectsWanted)
        {
            return !decision.Deny;
        }

        string path = PathFor(layout);
        try
        {
            layout.PrepareIndexFile(path);
            using PathScopedMutex? lease = PathScopedMutex.TryAcquire(
                "Guard", path, StoreLockTimeoutMilliseconds);
            if (lease is null)
            {
                return !decision.Deny;
            }

            StateFile file = Read(path);
            if (countersWanted)
            {
                file = file with
                {
                    Counters = file.Counters.With(
                        decision.Outcome, decision.Deny, elapsedMilliseconds),
                };
            }

            if (redirectsWanted)
            {
                Dictionary<string, int> paths = file.Redirects.TryGetValue(epochKey, out var existing)
                    ? existing
                    : [];
                foreach (GuardTarget target in decision.Targets.Where(t => t.Expensive))
                {
                    string key = PathKey(target.Path);
                    paths[key] = paths.TryGetValue(key, out int count) ? count + 1 : 1;
                }

                // Never deny unless every repeat marker will survive this write.
                if (paths.Count > MaxTrackedPathsPerEpoch
                    || (!file.Redirects.ContainsKey(epochKey)
                        && file.Redirects.Count >= MaxTrackedEpochs))
                {
                    return false;
                }
                file.Redirects[epochKey] = paths;
                TrimEpochs(file.Redirects);
            }

            Write(layout, path, file);
            return true;
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException or JsonException
                or WaitHandleCannotBeOpenedException)
        {
            // A denial without its repeat marker can cause an infinite loop.
            return !decision.Deny;
        }
    }

    /// <summary>Drops the redirect bookkeeping of one epoch.</summary>
    public static void ForgetEpoch(RepoLayout layout, string epochKey)
    {
        string path = PathFor(layout);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using PathScopedMutex? lease = PathScopedMutex.TryAcquire(
                "Guard", path, StoreLockTimeoutMilliseconds);
            if (lease is null)
            {
                return;
            }

            StateFile file = Read(path);
            if (file.Redirects.Remove(epochKey))
            {
                Write(layout, path, file);
            }
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException or JsonException
                or WaitHandleCannotBeOpenedException)
        {
        }
    }

    /// <summary>
    /// A short digest of a repository-relative path. Enough to count repeats,
    /// useless to anyone reading the file for repository structure.
    /// </summary>
    private static string PathKey(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];

    private static void Trim(Dictionary<string, int> paths, int maximum)
    {
        if (paths.Count <= maximum)
        {
            return;
        }

        foreach (string key in paths.Keys
            .Order(StringComparer.Ordinal)
            .Take(paths.Count - maximum)
            .ToList())
        {
            paths.Remove(key);
        }
    }

    private static void TrimEpochs(Dictionary<string, Dictionary<string, int>> redirects)
    {
        foreach (string key in redirects.Keys
            .Order(StringComparer.Ordinal)
            .Take(Math.Max(0, redirects.Count - MaxTrackedEpochs))
            .ToList())
        {
            redirects.Remove(key);
        }
    }

    private static StateFile Read(string path)
    {
        if (!File.Exists(path))
        {
            return new StateFile();
        }

        try
        {
            StateFile? file = JsonSerializer.Deserialize<StateFile>(
                File.ReadAllText(path), SerializerOptions);
            return file is null || file.V != StateFile.CurrentVersion || file.Counters is null
                || file.Counters.Latency is null || file.Counters.Latency.Buckets is null
                || file.Redirects is null || file.Redirects.Any(pair => pair.Value is null)
                ? new StateFile() : file;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new StateFile();
        }
    }

    private static void Write(RepoLayout layout, string path, StateFile file)
    {
        layout.PrepareIndexFile(path);
        SafePaths.WriteAllTextAtomic(path, JsonSerializer.Serialize(file, SerializerOptions));
    }

    /// <summary>A human-readable one-line summary of the counters.</summary>
    public static string Describe(GuardCounters counters)
    {
        var parts = new List<string>
        {
            $"observed={counters.Observed.ToString(CultureInfo.InvariantCulture)}",
            $"allowed={counters.Allowed.ToString(CultureInfo.InvariantCulture)}",
            $"redirected={counters.Redirected.ToString(CultureInfo.InvariantCulture)}",
            $"denied={counters.Denied.ToString(CultureInfo.InvariantCulture)}",
            $"escalated={counters.Escalated.ToString(CultureInfo.InvariantCulture)}",
            $"unsupported={counters.Unsupported.ToString(CultureInfo.InvariantCulture)}",
            $"error={counters.Error.ToString(CultureInfo.InvariantCulture)}",
        };

        if (counters.Latency.QuantileUpperBoundMs(0.95) is { } p95)
        {
            parts.Add($"latency_p95<={p95.ToString(CultureInfo.InvariantCulture)}ms");
        }

        return string.Join(' ', parts);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record StateFile
    {
        public const int CurrentVersion = 1;

        public int V { get; init; } = CurrentVersion;

        public GuardCounters Counters { get; init; } = new();

        public Dictionary<string, Dictionary<string, int>> Redirects { get; init; } =
            new(StringComparer.Ordinal);
    }
}
