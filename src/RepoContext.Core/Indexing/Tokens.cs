using Microsoft.ML.Tokenizers;

namespace RepoContext.Core.Indexing;

/// <summary>
/// Counts tokens with a real BPE tokenizer (<c>o200k_base</c>) instead of a
/// bytes-per-token guess, so token budgets can be trusted (ADR 0010). The
/// vocabulary is embedded via the tokenizer data package: counting is fully
/// offline and deterministic. Counts are still an approximation for any
/// particular model family (typically within ~10-15 %), which is why the JSON
/// fields keep the name <c>estimated_tokens</c>.
/// </summary>
public static class Tokens
{
    private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(
        () => TiktokenTokenizer.CreateForEncoding("o200k_base"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Counts the tokens of <paramref name="text"/>; empty text is 0.</summary>
    public static int Count(string text) =>
        text.Length == 0 ? 0 : Tokenizer.Value.CountTokens(text);
}
