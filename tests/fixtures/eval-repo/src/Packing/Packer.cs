namespace Eval.Packing;

/// <summary>
/// Packs ranked candidates into a bundle under a token budget.
/// Deliberately shaped for the Release 1 evaluation corpus: the interesting
/// method sits far below the first source-ordered symbols so a query-blind
/// outline cap would hide it.
/// </summary>
public sealed class Packer
{
    private readonly int _maxItems;
    private readonly TokenCounter _counter;

    /// <summary>Creates a packer with a hard item ceiling.</summary>
    public Packer(int maxItems, TokenCounter counter)
    {
        _maxItems = maxItems;
        _counter = counter;
    }

    /// <summary>Number of items admitted so far.</summary>
    public int Admitted { get; private set; }

    /// <summary>Resets the packer for a new run.</summary>
    public void Reset()
    {
        Admitted = 0;
    }

    /// <summary>Whether another item may be admitted at all.</summary>
    public bool HasRoom()
    {
        return Admitted < _maxItems;
    }

    /// <summary>Normalises a candidate path for stable ordering.</summary>
    public static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>Compares two candidates by score, then path.</summary>
    public static int CompareCandidates(Candidate left, Candidate right)
    {
        int byScore = right.Score.CompareTo(left.Score);
        return byScore != 0 ? byScore : string.CompareOrdinal(left.Path, right.Path);
    }

    /// <summary>Sorts candidates deterministically in place.</summary>
    public static void SortCandidates(List<Candidate> candidates)
    {
        candidates.Sort(CompareCandidates);
    }

    /// <summary>Drops candidates whose score is not positive.</summary>
    public static List<Candidate> DropUnscored(List<Candidate> candidates)
    {
        return candidates.FindAll(c => c.Score > 0);
    }

    /// <summary>Sums the raw content tokens of a candidate set.</summary>
    public int SumContentTokens(List<Candidate> candidates)
    {
        int total = 0;
        foreach (Candidate candidate in candidates)
        {
            total += _counter.Count(candidate.Text);
        }

        return total;
    }

    /// <summary>Describes the packer for diagnostics.</summary>
    public override string ToString()
    {
        return $"Packer(max={_maxItems}, admitted={Admitted})";
    }

    /// <summary>Counts candidates whose path sits under a directory prefix.</summary>
    public static int CountUnder(List<Candidate> candidates, string prefix)
    {
        return candidates.FindAll(c => c.Path.StartsWith(prefix, StringComparison.Ordinal)).Count;
    }

    /// <summary>Returns the distinct directories represented by the candidates.</summary>
    public static List<string> DistinctDirectories(List<Candidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Candidate candidate in candidates)
        {
            int slash = candidate.Path.LastIndexOf('/');
            seen.Add(slash < 0 ? string.Empty : candidate.Path[..slash]);
        }

        return [.. seen.Order(StringComparer.Ordinal)];
    }

    /// <summary>Formats a candidate for a human-readable listing.</summary>
    public static string Describe(Candidate candidate)
    {
        return $"{candidate.Path} ({candidate.Score:F4})";
    }

    /// <summary>Joins the reasons of a candidate into a stable string.</summary>
    public static string JoinReasons(Candidate candidate)
    {
        return string.Join(",", candidate.Reasons);
    }

    /// <summary>
    /// Packs candidates into the budget, admitting each item only when its full
    /// charged cost still fits. There is no first-item exception: an oversized
    /// candidate is skipped so smaller relevant ones further down still land.
    /// </summary>
    public List<Candidate> Budget(List<Candidate> candidates, int budgetTokens)
    {
        var admitted = new List<Candidate>();
        int used = 0;

        foreach (Candidate candidate in candidates)
        {
            if (!HasRoom())
            {
                break;
            }

            int cost = _counter.Count(candidate.Text) + EnvelopeTokens(candidate);
            if (used + cost > budgetTokens)
            {
                continue;
            }

            admitted.Add(candidate);
            used += cost;
            Admitted++;
        }

        return admitted;
    }

    /// <summary>
    /// The per-item framing an agent pays for beyond the content itself: the
    /// path, the reason list and a flat allowance for field names and numbers.
    /// </summary>
    public int EnvelopeTokens(Candidate candidate)
    {
        return 40 + _counter.Count(candidate.Path) + _counter.Count(string.Join(",", candidate.Reasons));
    }
}
