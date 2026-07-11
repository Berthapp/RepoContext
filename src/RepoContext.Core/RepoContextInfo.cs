namespace RepoContext.Core;

/// <summary>
/// Well-known constants shared across the RepoContext core and CLI.
/// </summary>
public static class RepoContextInfo
{
    /// <summary>
    /// Version of the machine-readable JSON output contract. Every JSON result
    /// emitted by the CLI carries this under <c>schema_version</c> so that
    /// consuming agents can detect breaking changes. Starts at 1; v2 adds the
    /// M6 token-frugal protocol (real token counts, hashes/state, detail
    /// levels, outline/changed documents — ADR 0010).
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>The name of the index directory created inside a repository.</summary>
    public const string IndexDirectoryName = ".repoctx";

    /// <summary>The name of the per-repository configuration file.</summary>
    public const string ConfigFileName = "repoctx.config.json";
}
