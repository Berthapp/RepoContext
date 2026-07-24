using RepoContext.Core.Identity;

namespace RepoContext.Core.Tests.Identity;

/// <summary>
/// The Q1 receipt contract (ADR 0015): what must invalidate a receipt, and —
/// just as importantly — what must not.
/// </summary>
public class ReceiptTests
{
    private static string Sample(
        string path = "src/a.cs", string hash = "content-hash-1", string detail = "slices",
        EvidenceUnitKind kind = EvidenceUnitKind.Span, int start = 10, int end = 20,
        string symbol = "Budget", string evidence = "void Budget() {}") =>
        Receipt.For(path, hash, detail, kind, start, end, symbol, evidence);

    [Fact]
    public void Receipt_IsFixedLengthBase64Url()
    {
        string receipt = Sample();

        Assert.Equal(Receipt.EncodedLength, receipt.Length);
        Assert.True(Receipt.IsWellFormed(receipt));
        Assert.DoesNotContain('=', receipt);
        Assert.DoesNotContain('+', receipt);
        Assert.DoesNotContain('/', receipt);
    }

    [Fact]
    public void Receipt_IsDeterministic()
    {
        Assert.Equal(Sample(), Sample());
    }

    /// <summary>Every component of the bound unit must change the value.</summary>
    [Fact]
    public void Receipt_ChangesForEveryBoundComponent()
    {
        string baseline = Sample();

        Assert.NotEqual(baseline, Sample(path: "src/b.cs"));
        Assert.NotEqual(baseline, Sample(hash: "content-hash-2"));
        Assert.NotEqual(baseline, Sample(detail: "outline"));
        Assert.NotEqual(baseline, Sample(kind: EvidenceUnitKind.Symbol));
        Assert.NotEqual(baseline, Sample(start: 11));
        Assert.NotEqual(baseline, Sample(end: 21));
        Assert.NotEqual(baseline, Sample(symbol: "EnvelopeTokens"));
        Assert.NotEqual(baseline, Sample(evidence: "void Budget() { return; }"));
    }

    /// <summary>
    /// The critical safety property: two different ranges of the same file at the
    /// same content version have different receipts, so one can never suppress
    /// the other.
    /// </summary>
    [Fact]
    public void DifferentRangesOfTheSameFile_HaveDifferentReceipts()
    {
        string first = Sample(start: 10, end: 20, symbol: "Budget", evidence: "a");
        string second = Sample(start: 30, end: 40, symbol: "EnvelopeTokens", evidence: "b");

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// A receipt is stateless and repository-independent by construction: the
    /// same unit copied elsewhere yields the same value. That is safe, because
    /// the value binds the content itself.
    /// </summary>
    [Fact]
    public void IdenticalUnitInAnotherRepository_YieldsTheSameReceipt()
    {
        Assert.Equal(
            Receipt.For("src/a.cs", "h", "slices", EvidenceUnitKind.Span, 1, 5, "F", "body"),
            Receipt.For("src/a.cs", "h", "slices", EvidenceUnitKind.Span, 1, 5, "F", "body"));
    }

    /// <summary>Backslash and forward-slash paths describe the same unit.</summary>
    [Fact]
    public void Receipt_NormalisesPathSeparators()
    {
        Assert.Equal(
            Receipt.For("src/x/a.cs", "h", "slices", EvidenceUnitKind.Span, 1, 5, "F", "b"),
            Receipt.For("src\\x\\a.cs", "h", "slices", EvidenceUnitKind.Span, 1, 5, "F", "b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("has spaces in it aaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("contains+plus/and/slash+aaaaaaaaaaaaaaaaaaa")]
    public void MalformedValues_AreRejected(string? value)
    {
        Assert.False(Receipt.IsWellFormed(value));
    }

    [Fact]
    public void WellFormedButUnknownValue_IsStillWellFormed()
    {
        // Shape validation and matching are separate concerns: a syntactically
        // valid receipt that matches nothing simply suppresses nothing.
        Assert.True(Receipt.IsWellFormed(new string('A', Receipt.EncodedLength)));
    }
}
