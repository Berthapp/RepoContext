using RepoContext.Core.Context;
using RepoContext.Core.Indexing;
using RepoContext.Core.Outline;

namespace RepoContext.Core.Stats;

/// <summary>
/// Computes the full-read tokens credited as replaced (ADR 0011). Only content
/// actually delivered (slices, non-empty outline skeletons) and re-reads the
/// caller declares avoided (unchanged markers) count. Pointers and discovery
/// answers replace nothing, whatever their guidance value.
/// </summary>
public static class UsageMeter
{
    /// <summary>
    /// Full-read tokens credited as replaced by a context bundle. Items that
    /// carry content have their full-read cost in
    /// <see cref="ContextItem.FileTokens"/>; unchanged markers deliberately omit
    /// it (they are zero-cost on the wire), so it is resolved via
    /// <paramref name="fullReadTokens"/>.
    /// </summary>
    public static int ReplacedTokens(ContextResult result, Func<string, int?> fullReadTokens)
    {
        int replaced = 0;
        foreach (ContextItem item in result.Items)
        {
            if (item.Unchanged)
            {
                replaced += fullReadTokens(item.Path) ?? 0;
            }
            else if (item.FileTokens is { } fullRead && CarriesContent(item))
            {
                replaced += fullRead;
            }
        }

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

    /// <summary>
    /// Full re-read tokens credited as replaced by delivered patch hunks
    /// (<c>changed --patch</c>). Only files whose delta actually carries
    /// hunks count — a plain status line replaces nothing.
    /// </summary>
    public static int PatchReplacedTokens(ChangedResult result) =>
        result.Changed.Sum(c => c.Hunks is { Count: > 0 } ? c.FileTokens ?? 0 : 0);

    private static bool CarriesContent(ContextItem item) =>
        item.Snippet is { Length: > 0 } || item.Symbols is { Count: > 0 };
}
