namespace RepoContext.Core.Context;

/// <summary>An explicit purpose for ordering otherwise eligible context evidence.</summary>
public enum ContextIntent { Fix, Explain, Review }

public static class ContextIntentParser
{
    public static bool TryParse(string? value, out ContextIntent? intent)
    {
        intent = value?.ToLowerInvariant() switch
        {
            "fix" => ContextIntent.Fix,
            "explain" => ContextIntent.Explain,
            "review" => ContextIntent.Review,
            _ => null,
        };
        return value is null || intent is not null;
    }
}

/// <summary>
/// Diagnostics describe the generated, scoped candidate pool, not every indexed
/// file. Samples cover positive-scoring omitted files only; reuse is reported by
/// the existing reuse fields. Samples may shrink to fit the exact response budget.
/// </summary>
public sealed record SelectionDiagnostics(
    int Candidates, int Eligible, IReadOnlyList<string>? Scope,
    int Omitted, int Unlisted, IReadOnlyList<SelectionOmission> Samples);

public sealed record SelectionOmission(
    string Path, int Rank, double Score, string Reason, SelectionLookup NextLookup);

/// <summary>Structured arguments, never a shell-escaped command string.</summary>
public sealed record SelectionLookup(string Command, string File);
