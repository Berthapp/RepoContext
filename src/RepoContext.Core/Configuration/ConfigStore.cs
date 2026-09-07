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
    public static RepoctxConfig Deserialize(string json)
    {
        RepoctxConfig config = JsonSerializer.Deserialize<RepoctxConfig>(json, RepoctxConfig.SerializerOptions)
            ?? throw new JsonException("Configuration must be an object, not null.");
        Validate(config);
        return config;
    }

    public static void Validate(RepoctxConfig config)
    {
        static void Strings(IEnumerable<string>? values, string name)
        {
            if (values is null || values.Any(string.IsNullOrWhiteSpace))
                throw new JsonException($"{name} must be an array of non-empty strings (an empty array is allowed).");
        }
        Strings(config.Include, "include");
        Strings(config.Exclude, "exclude");
        Strings(config.SensitiveFiles, "sensitiveFiles");
        if (config.Indexing is null || config.Artifacts is null || config.Ranking?.Weights is null
            || config.Ranking.Synonyms is null || config.Tokens is null || config.Pricing is null)
            throw new JsonException("Configuration sections must be objects, not null.");
        if (config.Indexing.MaxFileSizeKb <= 0 || config.Indexing.MaxFileSizeKb > int.MaxValue / 1024)
            throw new JsonException("indexing.maxFileSizeKb must be positive and at most 2097151.");
        if (config.Artifacts.MaxRefsPerFile < 0)
            throw new JsonException("artifacts.maxRefsPerFile must be non-negative.");
        Strings(config.Artifacts.KeyPatterns, "artifacts.keyPatterns");
        foreach ((string key, IReadOnlyList<string> values) in config.Ranking.Synonyms)
            Strings(values, $"ranking.synonyms.{key}");
    }

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
        "index_config.v2",
        Canonical.JoinRecords(config.Include),
        Canonical.JoinRecords(config.Exclude),
        config.RespectGitignore ? "true" : "false",
        Canonical.JoinRecords(config.SensitiveFiles),
        config.Indexing.MaxFileSizeKb.ToString(System.Globalization.CultureInfo.InvariantCulture),
        config.Indexing.IncludeTests ? "true" : "false",
        config.Indexing.IncludeDocs ? "true" : "false",
        // Artifact settings decide which references are extracted and stored,
        // so they belong to the stored-corpus identity, not to live ranking.
        Canonical.JoinRecords(config.Artifacts.KeyPatterns),
        config.Artifacts.LinkPaths ? "true" : "false",
        config.Artifacts.LinkSymbols ? "true" : "false",
        config.Artifacts.MaxRefsPerFile.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string Invariant(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
