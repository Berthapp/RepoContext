namespace RepoContext.Core.Storage;

/// <summary>Display form of content and state hashes in JSON output.</summary>
public static class Hashes
{
    /// <summary>
    /// Hashes are emitted truncated to this many hex characters: enough to be
    /// unambiguous as an echo token (agents pass them back via
    /// <c>--known path@hash</c>), short enough not to waste the tokens the
    /// hash exists to save.
    /// </summary>
    public const int ShortLength = 12;

    /// <summary>Truncates a full hex hash to its display form.</summary>
    public static string Short(string hash) =>
        hash.Length <= ShortLength ? hash : hash[..ShortLength];

    /// <summary>
    /// Whether a caller-supplied hash matches a stored one. Prefix matching
    /// (minimum 8 characters) lets agents echo the short form from output.
    /// </summary>
    public static bool Matches(string stored, string given) =>
        given.Length >= 8 && stored.StartsWith(given, StringComparison.OrdinalIgnoreCase);
}
