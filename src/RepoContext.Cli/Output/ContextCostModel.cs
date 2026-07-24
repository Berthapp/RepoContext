using RepoContext.Core.Context;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;

namespace RepoContext.Cli.Output;

/// <summary>
/// The concrete Q3 cost oracle: renders a tentative context result through the
/// real renderer, tokenizes the exact surface the caller will emit, and applies
/// the configured deterministic model-family calibration (ADR 0012/0016).
/// </summary>
/// <remarks>
/// Both the CLI and the MCP server construct one of these, which is what makes
/// "the same core packing behaviour" literally true rather than merely intended —
/// there is one renderer, one tokenizer and one packer behind both surfaces.
/// The two surfaces differ only in whether the trailing CLI newline is counted.
/// </remarks>
public sealed class ContextCostModel : IResponseCostModel
{
    private readonly OutputFormat _format;
    private readonly TokenScale _scale;

    private ContextCostModel(OutputFormat format, string surface, TokenScale scale)
    {
        _format = format;
        _scale = scale;
        Surface = surface;
    }

    /// <inheritdoc />
    public string Surface { get; }

    /// <summary>
    /// Measures CLI stdout, including its single trailing newline, in the
    /// configured token profile.
    /// </summary>
    public static ContextCostModel ForCli(
        OutputFormat format, TokenScale scale = default) =>
        new(format, Surfaces.Cli, scale);

    /// <summary>
    /// Measures the model-visible MCP text content block in the configured token
    /// profile. The JSON-RPC envelope around it is transport overhead reported
    /// separately by the evaluation harness and is deliberately not part of the
    /// per-call response budget.
    /// </summary>
    public static ContextCostModel ForMcpText(TokenScale scale = default) =>
        new(OutputFormat.Json, Surfaces.McpText, scale);

    /// <inheritdoc />
    public int Measure(ContextResult result) => _scale.Apply(Tokens.Count(SurfaceText(result)));

    /// <summary>The exact text emitted at this surface, for measuring or asserting.</summary>
    public string SurfaceText(ContextResult result)
    {
        string rendered = ContextOutput.Render(result, _format, Surface);

        // Mirrors CommandSupport.WriteRendered: stdout carries exactly one
        // trailing newline, and the budget must account for it.
        return Surface == Surfaces.Cli && !rendered.EndsWith('\n')
            ? rendered + "\n"
            : rendered;
    }
}
