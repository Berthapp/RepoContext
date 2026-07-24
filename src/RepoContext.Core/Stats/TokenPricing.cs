using System.Globalization;
using RepoContext.Core.Configuration;

namespace RepoContext.Core.Stats;

/// <summary>
/// Optional money view over token savings (ADR 0012). RepoContext ships no
/// built-in rates — prices change and vary by model — so this is active only
/// when <c>pricing.inputPerMtok</c> is set in config. Net saved tokens are an
/// input-side figure (context fed, or not fed, to the model), so the input
/// price is the right multiplier. Pure: identical inputs ⇒ identical text.
/// </summary>
public readonly record struct TokenPricing(double? InputPerMtok, string Currency)
{
    /// <summary>Whether a money estimate can be produced.</summary>
    public bool Enabled => InputPerMtok is > 0;

    /// <summary>Resolves pricing from config; disabled when no rate is set.</summary>
    public static TokenPricing From(RepoctxConfig config) =>
        new(config.Pricing.InputPerMtok, config.Pricing.Currency);

    /// <summary>
    /// Formats the money value of <paramref name="tokens"/> at the input rate,
    /// e.g. <c>$0.37</c> or <c>-$0.05</c>; null when pricing is disabled.
    /// Currency is rendered as a bare <c>$</c> for USD, else a code prefix.
    /// </summary>
    public string? Format(long tokens)
    {
        if (InputPerMtok is not { } rate || rate <= 0)
        {
            return null;
        }

        // Round first, then derive the sign, so a value that rounds to zero
        // never prints a stray "-$0.00".
        double value = Math.Round(tokens / 1_000_000.0 * rate, 2, MidpointRounding.AwayFromZero);
        string symbol = string.Equals(Currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? "$"
            : (Currency ?? "USD") + " ";
        string sign = value < 0 ? "-" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{symbol}{Math.Abs(value):0.00}");
    }
}
