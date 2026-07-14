using RepoContext.Core.Context;

namespace RepoContext.Core.Stats;

/// <summary>
/// Computes the full-read tokens a response replaced (ADR 0011). The ledger
/// is conservative: only content actually delivered (slices, outline
/// skeletons) and re-reads actually avoided (unchanged markers) count;
/// pointers and discovery answers replace nothing, whatever their guidance
/// value.
/// </summary>
public static class UsageMeter
{
    /// <summary>
    /// Full-read tokens replaced by a context bundle. Items that carry
    /// content have their full-read cost in <see cref="ContextItem.FileTokens"/>;
    /// unchanged markers deliberately omit it (they are zero-cost on the
    /// wire), so it is resolved via <paramref name="fullReadTokens"/>.
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
            else if (item.FileTokens is { } fullRead)
            {
                replaced += fullRead;
            }
        }

        return replaced;
    }
}
