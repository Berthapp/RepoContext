namespace Eval.Packing;

/// <summary>A ranked candidate considered by the packer.</summary>
public sealed record Candidate(string Path, double Score, string Text, IReadOnlyList<string> Reasons);

/// <summary>Counts tokens of a text deterministically and offline.</summary>
public sealed class TokenCounter
{
    private readonly int _charsPerToken;

    /// <summary>Creates a counter with the given characters-per-token ratio.</summary>
    public TokenCounter(int charsPerToken)
    {
        _charsPerToken = charsPerToken;
    }

    /// <summary>Counts the tokens of a text; empty text costs nothing.</summary>
    public int Count(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        return (text.Length + _charsPerToken - 1) / _charsPerToken;
    }
}
