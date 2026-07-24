using RepoContext.Core.Identity;

namespace RepoContext.Core.Tests.Identity;

/// <summary>
/// The Q4 fingerprint contract (ADR 0012). Each test pins one property of the
/// canonical byte layout, so a change to the layout has to be a deliberate edit
/// here rather than a silent identity drift in the field.
/// </summary>
public class FingerprintTests
{
    private const string ProducerA = "scan1.dec1.par1.chk1.tok1.gph1";
    private const string ProducerB = "scan1.dec1.par2.chk1.tok1.gph1";

    [Fact]
    public void ContentState_IsOrderIndependent_AndSeparatorSafe()
    {
        string a = Fingerprints.ContentState([("src/a.ts", "hash-a"), ("src/b.ts", "hash-b")]);
        string b = Fingerprints.ContentState([("src/b.ts", "hash-b"), ("src/a.ts", "hash-a")]);

        Assert.Equal(a, b);

        // A path/hash boundary cannot be forged by moving characters across it.
        Assert.NotEqual(
            Fingerprints.ContentState([("src/a.ts", "hash-ab")]),
            Fingerprints.ContentState([("src/a.tsh", "ash-ab")]));
    }

    [Fact]
    public void ContentState_NormalisesPathSeparators()
    {
        Assert.Equal(
            Fingerprints.ContentState([("src/nested/a.ts", "h")]),
            Fingerprints.ContentState([("src\\nested\\a.ts", "h")]));
    }

    [Fact]
    public void ContentState_ChangesWhenAnyContentChanges()
    {
        Assert.NotEqual(
            Fingerprints.ContentState([("src/a.ts", "hash-a")]),
            Fingerprints.ContentState([("src/a.ts", "hash-a2")]));
    }

    /// <summary>
    /// The heart of Q4: a producer, schema or config change must move the
    /// analysis fingerprint even though not one byte of source moved.
    /// </summary>
    [Theory]
    [InlineData("content", "c2", ProducerA, 4, "cfg1")]
    [InlineData("producer", "c1", ProducerB, 4, "cfg1")]
    [InlineData("schema", "c1", ProducerA, 5, "cfg1")]
    [InlineData("config", "c1", ProducerA, 4, "cfg2")]
    public void AnalysisState_MovesForEveryRelevantInput(
        string mutated, string content, string producer, int schema, string config)
    {
        string baseline = Fingerprints.AnalysisState("c1", ProducerA, 4, "cfg1");
        string changed = Fingerprints.AnalysisState(content, producer, schema, config);

        Assert.NotEqual(baseline, changed);
        Assert.False(string.IsNullOrEmpty(mutated));
    }

    [Fact]
    public void AnalysisState_IsStableForIdenticalInputs()
    {
        Assert.Equal(
            Fingerprints.AnalysisState("c1", ProducerA, 4, "cfg1"),
            Fingerprints.AnalysisState("c1", ProducerA, 4, "cfg1"));
    }

    /// <summary>
    /// Worktree state must distinguish two different files added at the same
    /// path — otherwise a delta fingerprint could collide across edits.
    /// </summary>
    [Fact]
    public void WorktreeState_DistinguishesDifferentContentAtTheSamePath()
    {
        string first = Fingerprints.WorktreeState("base", [("added", "src/new.ts", "hash-1")]);
        string second = Fingerprints.WorktreeState("base", [("added", "src/new.ts", "hash-2")]);

        Assert.NotEqual(first, second);
    }

    /// <summary>A deletion is explicitly marked, never encoded as empty content.</summary>
    [Fact]
    public void WorktreeState_MarksDeletionDistinctlyFromEmptyContent()
    {
        string deleted = Fingerprints.WorktreeState("base", [("deleted", "src/a.ts", null)]);
        string empty = Fingerprints.WorktreeState("base", [("added", "src/a.ts", string.Empty)]);

        Assert.NotEqual(deleted, empty);
    }

    [Fact]
    public void WorktreeState_IsOrderIndependent_AndTracksTheIndexedBase()
    {
        string a = Fingerprints.WorktreeState(
            "base", [("modified", "src/b.ts", "h2"), ("added", "src/a.ts", "h1")]);
        string b = Fingerprints.WorktreeState(
            "base", [("added", "src/a.ts", "h1"), ("modified", "src/b.ts", "h2")]);

        Assert.Equal(a, b);

        // The same delta over a different indexed base is a different state.
        Assert.NotEqual(a, Fingerprints.WorktreeState("other", [("added", "src/a.ts", "h1")]));
    }

    /// <summary>Evidence identity separates the analysed query from the options and the units.</summary>
    [Fact]
    public void EvidenceId_MovesForQuery_Options_AndEvidence()
    {
        string baseline = Fingerprints.EvidenceId("a1", "q", "opts", ["u1"]);

        Assert.NotEqual(baseline, Fingerprints.EvidenceId("a1", "q2", "opts", ["u1"]));
        Assert.NotEqual(baseline, Fingerprints.EvidenceId("a1", "q", "opts2", ["u1"]));
        Assert.NotEqual(baseline, Fingerprints.EvidenceId("a1", "q", "opts", ["u2"]));
        Assert.NotEqual(baseline, Fingerprints.EvidenceId("a2", "q", "opts", ["u1"]));
        Assert.Equal(baseline, Fingerprints.EvidenceId("a1", "q", "opts", ["u1"]));
    }

    /// <summary>Evidence order is part of the identity: a reordering is a different answer.</summary>
    [Fact]
    public void EvidenceId_IsOrderSensitive()
    {
        Assert.NotEqual(
            Fingerprints.EvidenceId("a", "q", "o", ["u1", "u2"]),
            Fingerprints.EvidenceId("a", "q", "o", ["u2", "u1"]));
    }

    /// <summary>
    /// Representation identity covers the encoding, not the evidence: the same
    /// evidence rendered to a different format or surface is a different
    /// representation.
    /// </summary>
    [Fact]
    public void RepresentationId_MovesForFormatProfileEncodingAndSurface()
    {
        string baseline = Fingerprints.RepresentationId(
            "e1", 3, "json", "default", "utf-8", Surfaces.Core, "{}");

        Assert.NotEqual(baseline, Fingerprints.RepresentationId(
            "e1", 3, "md", "default", "utf-8", Surfaces.Core, "{}"));
        Assert.NotEqual(baseline, Fingerprints.RepresentationId(
            "e1", 3, "json", "agent", "utf-8", Surfaces.Core, "{}"));
        Assert.NotEqual(baseline, Fingerprints.RepresentationId(
            "e1", 3, "json", "default", "utf-16", Surfaces.Core, "{}"));
        Assert.NotEqual(baseline, Fingerprints.RepresentationId(
            "e1", 3, "json", "default", "utf-8", Surfaces.McpText, "{}"));
        Assert.NotEqual(baseline, Fingerprints.RepresentationId(
            "e1", 4, "json", "default", "utf-8", Surfaces.Core, "{}"));
        Assert.NotEqual(baseline, Fingerprints.RepresentationId(
            "e1", 3, "json", "default", "utf-8", Surfaces.Core, "{\"a\":1}"));
    }

    /// <summary>
    /// The benchmark-only transport identity excludes the volatile JSON-RPC
    /// request ID by construction: it is simply not an input.
    /// </summary>
    [Fact]
    public void TransportProfileId_CoversProtocolAndToolSchemaOnly()
    {
        string baseline = Fingerprints.TransportProfileId("r1", "2025-06-18", "tools-v1");

        Assert.Equal(baseline, Fingerprints.TransportProfileId("r1", "2025-06-18", "tools-v1"));
        Assert.NotEqual(baseline, Fingerprints.TransportProfileId("r1", "2025-06-18", "tools-v2"));
        Assert.NotEqual(baseline, Fingerprints.TransportProfileId("r2", "2025-06-18", "tools-v1"));
    }

    /// <summary>Canonical hashing is unambiguous across field boundaries.</summary>
    [Fact]
    public void Canonical_FieldBoundaries_CannotBeForged()
    {
        Assert.NotEqual(Canonical.Hash("ab", "c"), Canonical.Hash("a", "bc"));
        Assert.NotEqual(
            Canonical.Hash("a\u001fb", "c"),
            Canonical.Hash("a", "b\u001fc"));
        Assert.NotEqual(
            Canonical.JoinRecords(["a\u001eb", "c"]),
            Canonical.JoinRecords(["a", "b\u001ec"]));
    }

    [Fact]
    public void ContentAndWorktreeStates_AreSafeForDelimiterCharactersInPaths()
    {
        Assert.NotEqual(
            Fingerprints.ContentState([("src/a\nhash", "b")]),
            Fingerprints.ContentState([("src/a", "hash\nb")]));
        Assert.NotEqual(
            Fingerprints.WorktreeState("base", [("added", "src/a:hash", "b")]),
            Fingerprints.WorktreeState("base", [("added", "src/a", "hash:b")]));
    }
}
