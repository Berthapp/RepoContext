using System.Text.Json;

namespace RepoContext.Core.Context;

/// <summary>
/// Persists the known-file set of an agent session (<c>--session</c>, ADR
/// 0012) under <c>.repoctx/sessions/&lt;name&gt;.json</c>, so the caller does
/// not have to echo <c>--known path@hash</c> lists back — those lists are
/// model *output* tokens, which cost a multiple of input tokens.
/// </summary>
/// <remarks>
/// The stored map has the same meaning as <see cref="ContextOptions.Known"/>:
/// path → short content hash of a file whose <b>full content</b> the caller
/// holds. Only delivered slices earn an entry (an outline skeleton or a bare
/// pointer is not the file); unchanged markers refresh the entry they matched.
/// The file is caller input, exactly like <c>--known</c>, so determinism is
/// preserved: identical index + query + session file ⇒ identical output.
/// Saving happens after the response is rendered and is best-effort.
/// </remarks>
public static class SessionStore
{
    private const int MaxNameLength = 64;

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
    /// Loads the known map of a session; an absent or unreadable file is an
    /// empty session (the caller merely re-pays for content, never breaks).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(RepoLayout layout, string name)
    {
        try
        {
            string path = PathFor(layout, name);
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            SessionFile? file = JsonSerializer.Deserialize<SessionFile>(
                File.ReadAllText(path), SerializerOptions);
            return new Dictionary<string, string>(
                file?.Known ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Folds a served bundle into the session: delivered slices are now held
    /// by the caller (record their hash), unchanged markers confirm existing
    /// entries. Pointer-only and outline items change nothing — the caller
    /// still does not hold those files. Best-effort: a failed write only
    /// means the next call re-pays.
    /// </summary>
    public static void Save(RepoLayout layout, string name, ContextResult result)
    {
        try
        {
            var known = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach ((string path, string hash) in Load(layout, name))
            {
                known[path] = hash;
            }

            foreach (ContextItem item in result.Items)
            {
                if (item.Snippet is { Length: > 0 } || item.Unchanged)
                {
                    known[item.Path] = item.Hash;
                }
            }

            string sessionPath = PathFor(layout, name);
            Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
            File.WriteAllText(sessionPath, JsonSerializer.Serialize(
                new SessionFile { Known = known }, SerializerOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Session persistence must never break a query.
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record SessionFile
    {
        public IDictionary<string, string> Known { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
