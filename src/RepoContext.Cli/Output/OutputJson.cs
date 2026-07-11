using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoContext.Cli.Output;

/// <summary>
/// The single serializer policy for every <c>--format json</c> document and MCP
/// tool result (ADR 0009). JSON is the machine-facing format — its primary
/// consumers are AI agents that pay per token — so documents are serialized
/// compact (no indentation, one line) and null-valued optional fields are
/// omitted. Humans use the <c>text</c>/<c>md</c> formats or <c>jq</c>.
/// </summary>
public static class OutputJson
{
    /// <summary>Compact, snake_case, null-omitting options shared by all output documents.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
