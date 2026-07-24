using Eval.Packing;

namespace Eval.Tests;

/// <summary>Tests for the budget packer.</summary>
public sealed class PackerTests
{
    /// <summary>An oversized candidate must not block smaller ones behind it.</summary>
    public void Budget_SkipsOversizedCandidate_AndAdmitsSmallerOnes()
    {
        var counter = new TokenCounter(4);
        var packer = new Packer(10, counter);
        var candidates = new List<Candidate>
        {
            new("src/huge.cs", 0.9, new string('x', 4000), new[] { "fts" }),
            new("src/small.cs", 0.5, "tiny", new[] { "fts" }),
        };

        List<Candidate> admitted = packer.Budget(candidates, 100);
        Assert(admitted.Count == 1);
    }

    /// <summary>Envelope tokens are charged on top of the content itself.</summary>
    public void EnvelopeTokens_ChargesPathAndReasons()
    {
        var counter = new TokenCounter(4);
        var packer = new Packer(10, counter);
        var candidate = new Candidate("src/a.cs", 1.0, "body", new[] { "fts" });

        Assert(packer.EnvelopeTokens(candidate) > 40);
    }

    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("assertion failed");
        }
    }
}
