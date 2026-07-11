namespace RepoContext.Core.Context;

/// <summary>
/// Dedupes reason lists and caps the only unbounded class, graph reasons
/// (ADR 0009/0010). Each graph reason carries a full neighbor path, and a hub
/// file can collect one per importer — dozens on real repositories — while the
/// consumer is an AI agent paying tokens for every one. The first
/// <see cref="MaxGraphReasons"/> (insertion order: strongest evidence first)
/// are kept; the rest fold into a single <c>graph:+N</c> summary right after
/// them, so the evidence type and its extent stay visible. The full edge list
/// remains available via <c>repoctx related</c>. All other reason kinds occur
/// at most once per item and pass through unchanged.
/// </summary>
internal static class ReasonCompression
{
    internal const int MaxGraphReasons = 2;

    internal static IReadOnlyList<string> Compress(List<string> reasons)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        int graphKept = 0, graphDropped = 0, lastGraph = -1;

        foreach (string r in reasons)
        {
            if (!seen.Add(r))
            {
                continue;
            }

            if (IsGraphReason(r))
            {
                if (graphKept == MaxGraphReasons)
                {
                    graphDropped++;
                    continue;
                }

                graphKept++;
                lastGraph = result.Count;
            }

            result.Add(r);
        }

        if (graphDropped > 0)
        {
            result.Insert(lastGraph + 1, $"graph:+{graphDropped}");
        }

        return result;
    }

    internal static bool IsGraphReason(string reason) =>
        reason.StartsWith("imports:", StringComparison.Ordinal)
        || reason.StartsWith("imported-by:", StringComparison.Ordinal)
        || reason.StartsWith("tested-by:", StringComparison.Ordinal)
        || reason.StartsWith("test-of:", StringComparison.Ordinal);
}
