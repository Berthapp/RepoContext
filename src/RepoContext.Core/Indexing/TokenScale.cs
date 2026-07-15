using System.Globalization;
using RepoContext.Core.Configuration;

namespace RepoContext.Core.Indexing;

/// <summary>
/// Query-time calibration of the raw <c>o200k_base</c> counts (ADR 0012).
/// The index always stores raw counts; the scale is applied where budgets are
/// charged and counts are reported, so switching profiles never requires a
/// re-index. Profiles carry measured average factors for other model
/// families' tokenizers (Claude tokenizes typical source to ~15-25 % more
/// tokens than o200k); an explicit numeric factor overrides the profile.
/// Deterministic: the scale is pure config input.
/// </summary>
public readonly record struct TokenScale
{
    /// <summary>Known profile names and their calibration factors.</summary>
    public static readonly IReadOnlyDictionary<string, double> Profiles =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["o200k"] = 1.0,
            ["openai"] = 1.0,
            ["claude"] = 1.2,
        };

    private TokenScale(double factor, string? label)
    {
        Factor = factor;
        Label = label;
    }

    /// <summary>The multiplier applied to raw counts; 0 (default struct) means identity.</summary>
    public double Factor { get; }

    /// <summary>
    /// Human/machine-readable name of the active calibration (profile name or
    /// <c>x&lt;factor&gt;</c>), null when counts are raw o200k.
    /// </summary>
    public string? Label { get; }

    /// <summary>The identity scale (raw o200k counts).</summary>
    public static TokenScale Identity => default;

    /// <summary>Whether this scale leaves counts unchanged.</summary>
    public bool IsIdentity => Factor is <= 0 or 1.0;

    /// <summary>
    /// Resolves the scale from config: an explicit <c>tokens.factor</c> wins,
    /// then a known <c>tokens.profile</c> name; anything else (including an
    /// unknown profile) is the identity, so a typo degrades to raw counts
    /// rather than to silently wrong ones.
    /// </summary>
    public static TokenScale From(RepoctxConfig config)
    {
        TokenOptions tokens = config.Tokens;
        if (tokens.Factor is { } factor and > 0)
        {
            return factor == 1.0
                ? Identity
                : new TokenScale(factor, "x" + factor.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (tokens.Profile is { Length: > 0 } profile
            && Profiles.TryGetValue(profile, out double known) && known != 1.0)
        {
            return new TokenScale(known, profile.ToLowerInvariant());
        }

        return Identity;
    }

    /// <summary>Applies the scale to a raw count (rounded up — budgets must not undershoot).</summary>
    public int Apply(int rawCount) =>
        IsIdentity || rawCount <= 0 ? rawCount : (int)Math.Ceiling(rawCount * Factor);
}
