using RepoContext.Core.Context;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;

namespace RepoContext.Cli.Output;

/// <summary>
/// The concrete Q3 cost oracle: renders a tentative context result through the
/// real renderer and tokenizes the exact surface the caller will emit (ADR 0013).
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

    private ContextCostModel(OutputFormat format, string surface)
    {
        _format = format;
        Surface = surface;
    }

    /// <inheritdoc />
    public string Surface { get; }

    /// <summary>Measures exact CLI stdout, including its single trailing newline.</summary>
    public static ContextCostModel ForCli(OutputFormat format) => new(format, Surfaces.Cli);

    /// <summary>
    /// Measures the model-visible MCP text content block. The JSON-RPC envelope
    /// around it is transport overhead reported separately by the evaluation
    /// harness and is deliberately not part of the per-call response budget.
    /// </summary>
    public static ContextCostModel ForMcpText() => new(OutputFormat.Json, Surfaces.McpText);

    /// <inheritdoc />
    public int Measure(ContextResult result) => Tokens.Count(SurfaceText(result));

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
