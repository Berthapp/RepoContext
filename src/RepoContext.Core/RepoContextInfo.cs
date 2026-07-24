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
    /// levels, outline/changed documents — ADR 0010); v3 adds the Release 1
    /// cost-efficiency protocol — per-unit reuse receipts, multi-span evidence,
    /// explicit content/projected-read token accounting and the versioned
    /// content/analysis/evidence/representation identities (ADR 0015).
    /// </summary>
    /// <remarks>
    /// v2 fields kept for one compatibility window and marked deprecated:
    /// top-level <c>state</c> and <c>estimated_tokens</c>, item
    /// <c>estimated_tokens</c>/<c>file_tokens</c>, and the single-span
    /// <c>start_line</c>/<c>end_line</c>/<c>snippet</c> trio. New consumers read
    /// the explicit v3 fields instead.
    /// </remarks>
    public const int SchemaVersion = 3;

    /// <summary>The name of the index directory created inside a repository.</summary>
    public const string IndexDirectoryName = ".repoctx";

    /// <summary>The name of the per-repository configuration file.</summary>
    public const string ConfigFileName = "repoctx.config.json";
}
