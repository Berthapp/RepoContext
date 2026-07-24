using System.Text.Json;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;

namespace RepoContext.Core.Configuration;

/// <summary>Reads and writes <c>repoctx.config.json</c> and hashes it.</summary>
public static class ConfigStore
{
    /// <summary>Serializes the config to its canonical JSON text (LF line endings).</summary>
    public static string Serialize(RepoctxConfig config) =>
        JsonSerializer.Serialize(config, RepoctxConfig.SerializerOptions);

    /// <summary>Deserializes config text, falling back to defaults for missing members.</summary>
    public static RepoctxConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<RepoctxConfig>(json, RepoctxConfig.SerializerOptions)
        ?? RepoctxConfig.CreateDefault();

    /// <summary>Loads the config from <paramref name="path"/>.</summary>
    public static RepoctxConfig Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Writes the config to <paramref name="path"/> with a trailing newline.</summary>
    public static void Save(string path, RepoctxConfig config) =>
        File.WriteAllText(path, Serialize(config) + "\n");

    /// <summary>
    /// A stable semantic hash of the complete effective analysis configuration.
    /// Map keys are ordinally sorted so construction/insertion order cannot move
    /// Q4 identities. Pricing is deliberately excluded because it changes only
    /// the local stats view, never selection, budgets, receipts or output.
    /// </summary>
    public static string ComputeHash(RepoctxConfig config)
    {
        TokenScale scale = TokenScale.From(config);
        double effectiveFactor = scale.IsIdentity ? 1.0 : scale.Factor;

        return Canonical.Hash(
            "effective_config.v2",
            ComputeIndexHash(config),
            Invariant(config.Ranking.Weights.Fts),
            Invariant(config.Ranking.Weights.Symbol),
            Invariant(config.Ranking.Weights.Graph),
            Invariant(config.Ranking.Weights.Path),
            Canonical.JoinRecords(config.Ranking.Synonyms
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Canonical.JoinRecords(
                [
                    pair.Key,
                    Canonical.JoinRecords(pair.Value),
                ]))),
            scale.Label ?? "o200k",
            Invariant(effectiveFactor));
    }

    /// <summary>
    /// Stable hash of only the configuration that determines the indexed corpus.
    /// Live ranking weights and synonyms change <c>analysis_state</c>, but do not
    /// force stored chunks/symbols/edges to be rebuilt.
    /// </summary>
    public static string ComputeIndexHash(RepoctxConfig config) => Canonical.Hash(
        "index_config.v1",
        Canonical.JoinRecords(config.Include),
        Canonical.JoinRecords(config.Exclude),
        config.RespectGitignore ? "true" : "false",
        Canonical.JoinRecords(config.SensitiveFiles),
        config.Indexing.MaxFileSizeKb.ToString(System.Globalization.CultureInfo.InvariantCulture),
        config.Indexing.IncludeTests ? "true" : "false",
        config.Indexing.IncludeDocs ? "true" : "false");

    private static string Invariant(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
