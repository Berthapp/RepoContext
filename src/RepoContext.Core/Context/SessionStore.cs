using System.Text.Json;
using RepoContext.Core.Identity;

namespace RepoContext.Core.Context;

/// <summary>
/// Persists the evidence an agent session already holds (<c>--session</c>, ADR
/// 0012/0015) under <c>.repoctx/sessions/&lt;name&gt;.json</c>, so the caller
/// does not have to echo known-file assertions or per-unit receipts.
/// </summary>
/// <remarks>
/// The stored known map has the same strict meaning as
/// <see cref="ContextOptions.Known"/>: path → content hash of a file whose
/// <b>full content</b> the caller holds. A delivered slice, outline, or pointer
/// never earns that assertion; each instead contributes only its exact receipt
/// to <see cref="SessionState.Seen"/>.
/// The file is caller input, exactly like <c>--known</c>, so determinism is
/// preserved: identical index + query + session file ⇒ identical output.
/// Saving happens after the response is rendered and is best-effort.
/// </remarks>
public static class SessionStore
{
    private const int MaxNameLength = 64;
    private const int StoreLockTimeoutMilliseconds = 3_000;

    /// <summary>Session names are file names; keep them boring and portable.</summary>
    public static bool IsValidName(string? name)
    {
        if (name is not { Length: > 0 and <= MaxNameLength } || name is "." or "..")
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The on-disk path of a session file.</summary>
    public static string PathFor(RepoLayout layout, string name) =>
        Path.Combine(layout.IndexDirectory, "sessions", name + ".json");

    /// <summary>
    /// Loads both whole-file assertions and exact evidence receipts. An absent
    /// or unreadable file is an empty session (the caller merely re-pays).
    /// </summary>
    public static SessionState LoadState(RepoLayout layout, string name)
    {
        try
        {
            string path = PathFor(layout, name);
            using PathScopedMutex? lease = PathScopedMutex.TryAcquire(
                "Session", path, StoreLockTimeoutMilliseconds);
            if (lease is null)
            {
                // Replacement is atomic, so a lock-free read still observes
                // either the complete prior state or the complete next state.
                // Prefer that safe snapshot over discarding useful context.
                return LoadStateUnlocked(path);
            }

            return LoadStateUnlocked(path);
        }
        catch (Exception e) when (
            e is IOException or JsonException or UnauthorizedAccessException
                or WaitHandleCannotBeOpenedException)
        {
            return SessionState.Empty;
        }
    }

    /// <summary>
    /// Compatibility accessor for callers interested only in true whole-file
    /// possession. New context callers should use <see cref="LoadState"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(RepoLayout layout, string name)
        => LoadState(layout, name).Known;

    /// <summary>
    /// Folds a served bundle into the session. Delivered slices, symbols, and
    /// pointers contribute only their exact receipts. Whole-file entries enter
    /// the known map only through <paramref name="assertedKnown"/>, which has
    /// the same explicit possession semantics as <c>--known</c>. Best-effort:
    /// a failed write only means the next call re-pays.
    /// </summary>
    public static void Save(
        RepoLayout layout,
        string name,
        ContextResult result,
        IReadOnlyDictionary<string, string>? assertedKnown = null,
        IReadOnlyList<string>? assertedSeen = null)
    {
        try
        {
            string sessionPath = PathFor(layout, name);
            using PathScopedMutex? lease = PathScopedMutex.TryAcquire(
                "Session", sessionPath, StoreLockTimeoutMilliseconds);
            if (lease is null)
            {
                return;
            }

            // The read and replacement are one transaction. Otherwise two
            // agents can both merge from the same snapshot and the later
            // replacement silently drops the first agent's evidence.
            SessionState existing = LoadStateUnlocked(sessionPath);
            var known = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach ((string path, string hash) in existing.Known)
            {
                known[path] = hash;
            }

            if (assertedKnown is not null)
            {
                foreach ((string path, string hash) in assertedKnown)
                {
                    known[path] = hash;
                }
            }

            var seen = new SortedSet<string>(existing.Seen, StringComparer.Ordinal);
            AddWellFormed(seen, assertedSeen);
            AddWellFormed(seen, result.Reused.Select(unit => unit.Receipt));
            foreach (ContextItem item in result.Items)
            {
                AddWellFormed(seen, UnitReceipts(item));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
            string serialized = JsonSerializer.Serialize(
                new SessionFile
                {
                    V = SessionFile.CurrentVersion,
                    Known = known,
                    Seen = seen.ToList(),
                }, SerializerOptions);
            ReplaceAtomically(sessionPath, serialized);
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException
                or WaitHandleCannotBeOpenedException)
        {
            // Session persistence must never break a query.
        }
    }

    private static SessionState LoadStateUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return SessionState.Empty;
        }

        SessionFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SessionFile>(
                File.ReadAllText(path), SerializerOptions);
        }
        catch (JsonException)
        {
            return SessionState.Empty;
        }

        // Pre-v2 sessions promoted slices to whole-file Known entries. There
        // is no way to distinguish those unsafe claims from explicit ones, so
        // migrate fail-closed: the caller re-pays once and only new v2 evidence
        // is persisted after the successful response.
        if (file?.V != SessionFile.CurrentVersion)
        {
            return SessionState.Empty;
        }

        return new SessionState
        {
            Known = new Dictionary<string, string>(
                file.Known, StringComparer.Ordinal),
            Seen = file.Seen
                .Where(Receipt.IsWellFormed)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static void ReplaceAtomically(string path, string content)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void AddWellFormed(ISet<string> target, IEnumerable<string>? receipts)
    {
        if (receipts is null)
        {
            return;
        }

        foreach (string receipt in receipts)
        {
            if (Receipt.IsWellFormed(receipt))
            {
                target.Add(receipt);
            }
        }
    }

    private static IEnumerable<string> UnitReceipts(ContextItem item)
    {
        if (item.Spans is { Count: > 0 } spans)
        {
            foreach (ContextSpan span in spans)
            {
                yield return span.Receipt;
            }
        }
        else if (item.Symbols is { Count: > 0 } symbols)
        {
            foreach (Outline.OutlineSymbol symbol in symbols)
            {
                if (symbol.Receipt is { } receipt)
                {
                    yield return receipt;
                }
            }
        }
        else if (item.Receipt is { } only)
        {
            yield return only;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record SessionFile
    {
        public const int CurrentVersion = 2;

        public int V { get; init; }

        public IDictionary<string, string> Known { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyList<string> Seen { get; init; } = [];
    }
}

/// <summary>Evidence persisted for one local agent session.</summary>
public sealed record SessionState
{
    public IReadOnlyDictionary<string, string> Known { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> Seen { get; init; } = [];

    public static SessionState Empty => new();
}
