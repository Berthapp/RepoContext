using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoContext.Core.Stats;

/// <summary>Where a recorded query came from.</summary>
public static class UsageSources
{
    public const string Cli = "cli";

    public const string Mcp = "mcp";
}

/// <summary>
/// One recorded query, as persisted to the local usage log (ADR 0011). The
/// ledger is deliberately simple: every response has a real token cost
/// (<see cref="Served"/>) and may have replaced full file reads the agent
/// would otherwise have paid for (<see cref="Replaced"/>). Net savings are
/// derived, never stored.
/// </summary>
public sealed record UsageRecord
{
    /// <summary>Record format version, for forward-compatible readers.</summary>
    [JsonRequired]
    public int V { get; init; } = 1;

    /// <summary>When the query ran (UTC). Never part of query output.</summary>
    public required DateTimeOffset Ts { get; init; }

    /// <summary>The CLI command name (context, outline, search, ...).</summary>
    public required string Command { get; init; }

    /// <summary>One of <see cref="UsageSources"/>.</summary>
    public required string Source { get; init; }

    /// <summary>Real (o200k) token count of the rendered response.</summary>
    public int Served { get; init; }

    /// <summary>
    /// Full-read tokens this response is credited with replacing: embedded
    /// slices and non-empty outline skeletons at the file's full-read cost,
    /// unchanged markers at the re-read they avoided. Zero for pure discovery
    /// responses.
    /// </summary>
    public int Replaced { get; init; }

    /// <summary>Result items in the response (context/outline only).</summary>
    public int? Files { get; init; }

    /// <summary>Zero-cost unchanged markers in the response (context only).</summary>
    public int? Unchanged { get; init; }

    /// <summary>Compact single-line serializer options for the JSONL log.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
