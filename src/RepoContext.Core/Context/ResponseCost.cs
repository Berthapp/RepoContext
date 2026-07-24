namespace RepoContext.Core.Context;

/// <summary>
/// The shared format-aware token-cost oracle (Q3, ADR 0013). Packing and
/// rendering must agree on what a response costs, so the packer does not
/// estimate: it asks the real renderer to serialize a tentative result and
/// tokenizes the exact surface boundary the caller will emit.
/// </summary>
/// <remarks>
/// <para>
/// The oracle lives behind an interface because the renderers are the source of
/// truth and they live in the CLI layer, while packing lives in the core. Both
/// the CLI and the MCP server pass a model backed by the <i>same</i> renderer,
/// which is what keeps the two surfaces one implementation rather than two.
/// </para>
/// <para>
/// There is no self-referential fixed point: the measured document deliberately
/// never contains its own token count (Q3 records exact totals out-of-band after
/// rendering), and <c>representation_id</c> is hashed with its own field omitted.
/// Measurement therefore terminates after the finite ranked candidate list.
/// </para>
/// </remarks>
public interface IResponseCostModel
{
    /// <summary>
    /// Exact tokens of the model-visible response for <paramref name="result"/>
    /// at this model's surface boundary — CLI stdout including its trailing
    /// newline, or the MCP text content block.
    /// </summary>
    int Measure(ContextResult result);

    /// <summary>The surface being measured, for reporting and identity purposes.</summary>
    string Surface { get; }
}
