using RepoContext.Core.Context;
using RepoContext.Core.Outline;

namespace RepoContext.Core.Stats;

/// <summary>
/// Computes the full-read tokens credited as replaced (ADR 0011). Only content
/// actually delivered (slices, non-empty outline skeletons) and re-reads the
/// caller declares avoided (acknowledged reuse) count. Pointers and discovery
/// answers replace nothing, whatever their guidance value.
/// </summary>
public static class UsageMeter
{
    /// <summary>
    /// Full-read tokens credited as replaced by a context bundle.
    /// </summary>
    /// <remarks>
    /// Two sources contribute. Items that carry embedded evidence credit the
    /// full-read cost they stand in for. Reuse contributes only when an explicit
    /// matching full-file possession claim objectively avoided a read. Span,
    /// symbol, and pointer receipts prove evidence possession, not a full-file
    /// read, and therefore receive no speculative full-read credit.
    /// </remarks>
    public static int ReplacedTokens(ContextResult result, Func<string, int?> fullReadTokens)
    {
        _ = fullReadTokens; // Retained for source compatibility with existing callers.
        int replaced = 0;
        var creditedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContextItem item in result.Items)
        {
            if (item.FileTokens is { } fullRead
                && CarriesContent(item)
                && creditedPaths.Add(item.Path))
            {
                replaced += fullRead;
            }
        }

        // Known-file acknowledgement takes precedence over evidence delivery in
        // the packer, so these paths cannot overlap delivered items. The total is
        // computed from the complete reuse set before its display prefix is cut.
        replaced += result.ReusedReadTokens;

        return replaced;
    }

    /// <summary>
    /// Full-read tokens credited as replaced by a standalone outline. Metadata
    /// for a file with no parsed symbols is still useful, but it is not a
    /// delivered skeleton and therefore cannot claim to replace reading the
    /// file.
    /// </summary>
    public static int OutlineReplacedTokens(OutlineResult result) =>
        result.Symbols.Count > 0 ? result.TokenCount : 0;

    private static bool CarriesContent(ContextItem item) =>
        item.Spans is { Count: > 0 } || item.Symbols is { Count: > 0 };
}
