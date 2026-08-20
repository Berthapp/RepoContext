using RepoContext.Core.Configuration;
using RepoContext.Core.Graph;
using RepoContext.Core.Parsing;
using RepoContext.Core.Scanning;

namespace RepoContext.Core.Tests.Graph;

/// <summary>
/// Reference extraction (ADR 0019). What is stored here decides what
/// <c>trace</c> can answer and which cross-artifact edges exist, so both the
/// findings and the deliberate non-findings are pinned.
/// </summary>
public class ReferenceExtractorTests
{
    private static ScannedFile File(string path, FileKind kind, SourceLanguage language) => new()
    {
        AbsolutePath = "/repo/" + path,
        RelativePath = path,
        Kind = kind,
        Language = language,
        SizeBytes = 0,
    };

    private static IReadOnlyList<FileReference> Extract(
        ScannedFile file, string content, ArtifactOptions? options = null)
    {
        using ILanguageParser parser = new TreeSitterParser();
        return new ReferenceExtractor(options ?? new ArtifactOptions())
            .Extract(file, content, parser);
    }

    private static IEnumerable<string> Values(IReadOnlyList<FileReference> refs, string kind) =>
        refs.Where(r => r.Kind == kind).Select(r => r.Value);

    [Fact]
    public void WorkItemKeys_AreFoundWithTheLineTheyAppearOn()
    {
        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "Refund policy.\nImplements PAY-142 and REQ-4711.\n");

        Assert.Equal(["PAY-142", "REQ-4711"], Values(refs, RefKind.Key).Order(StringComparer.Ordinal));
        Assert.All(refs.Where(r => r.Kind == RefKind.Key), r => Assert.Equal(2, r.Line));
    }

    /// <summary>
    /// The built-in shape is uppercase-only on purpose: a lowercase
    /// <c>word-123</c> is ordinary prose or a package name, and treating it as
    /// a work item would fill the reference index with noise. A team that
    /// really writes its keys in lower case adds a pattern for it.
    /// </summary>
    [Fact]
    public void LowercaseLookalikes_AreNotWorkItemKeys()
    {
        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "Uses left-pad and pay-142 in prose.\n");

        Assert.Empty(Values(refs, RefKind.Key));
    }

    [Fact]
    public void EncodingsAndStandards_AreNotWorkItemKeys()
    {
        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "Encoded as UTF-8, hashed with SHA-256, dated per ISO-8601, see RFC-2119.\n");

        Assert.Empty(Values(refs, RefKind.Key));
    }

    [Fact]
    public void ConfiguredKeyPatterns_ExtendTheBuiltInShape()
    {
        var options = new ArtifactOptions { KeyPatterns = ["SPEC_[0-9]{3}"] };

        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "Covered by SPEC_042.\n",
            options);

        Assert.Contains("SPEC_042", Values(refs, RefKind.Key));
    }

    [Fact]
    public void InvalidKeyPattern_IsIgnoredRatherThanFailingTheIndex()
    {
        var options = new ArtifactOptions { KeyPatterns = ["([unclosed"] };

        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "Implements PAY-142.\n",
            options);

        Assert.Equal(["PAY-142"], Values(refs, RefKind.Key));
    }

    [Fact]
    public void Links_AreNormalizedSoOnePageIsOneReference()
    {
        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "See https://Example.NET/wiki/Refund+Policy/#window and https://example.net/wiki/Refund+Policy.\n");

        Assert.Equal(["https://example.net/wiki/Refund+Policy"], Values(refs, RefKind.Link).Distinct());
    }

    [Fact]
    public void PathMentions_AreCapturedButNamespacesAreNot()
    {
        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "Implemented in `./src/billing/refund.ts`, configured by package.json. "
            + "Serialized with System.Text.Json, e.g. version 1.2.3.\n");

        Assert.Equal(
            ["package.json", "src/billing/refund.ts"],
            Values(refs, RefKind.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DocumentSymbols_NeedACaseHumpOrUnderscore()
    {
        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "The refund is recorded by AuditLog and bounded by MAX_WINDOW, not by policy.\n");

        List<string> symbols = [.. Values(refs, RefKind.Symbol)];
        Assert.Contains("AuditLog", symbols);
        Assert.Contains("MAX_WINDOW", symbols);
        Assert.DoesNotContain("policy", symbols);
        Assert.DoesNotContain("refund", symbols);
    }

    [Fact]
    public void SourceFiles_ContributeImportsAndTypeUsesRatherThanProseSymbols()
    {
        IReadOnlyList<FileReference> typescript = Extract(
            File("src/a.ts", FileKind.Source, SourceLanguage.TypeScript),
            "import { AuditLog } from \"./audit\";\nexport const x = 1;\n");
        Assert.Equal(["./audit"], Values(typescript, RefKind.Import));
        Assert.Empty(Values(typescript, RefKind.Symbol));

        IReadOnlyList<FileReference> csharp = Extract(
            File("src/A.cs", FileKind.Source, SourceLanguage.CSharp),
            "public sealed class A { private readonly AuditLog _log; }\n");
        Assert.Contains("AuditLog", Values(csharp, RefKind.Type));
        Assert.Empty(Values(csharp, RefKind.Symbol));
    }

    [Fact]
    public void ReferencesAreCappedPerKindAndOrderedDeterministically()
    {
        var options = new ArtifactOptions { MaxRefsPerFile = 2 };
        string content = string.Join('\n', ["ZED-9 first", "ABC-1 second", "MID-5 third"]);

        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown), content, options);

        // Sorted by value, then capped: the retained subset does not depend on
        // where in the file a reference happens to appear first.
        Assert.Equal(["ABC-1", "MID-5"], Values(refs, RefKind.Key));
        Assert.Equal(refs, Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown), content, options));
    }

    /// <summary>
    /// A pathological configured pattern must cost one file its keys, not the
    /// whole index run. <c>Regex.Matches</c> is lazy, so the timeout fires while
    /// the collection is enumerated - which is why the enumeration has to happen
    /// inside the guarded region.
    /// </summary>
    [Fact]
    public void ACatastrophicallyBacktrackingPattern_DoesNotAbortTheIndex()
    {
        var options = new ArtifactOptions { KeyPatterns = ["(a+)+$"] };
        string content = "PAY-142 " + new string('a', 40) + "b\n";

        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown), content, options);

        Assert.Equal(["PAY-142"], Values(refs, RefKind.Key));
    }

    /// <summary>
    /// Type references are the sole input to a C# file's import edges, so the
    /// artifact bound must not truncate them: doing so drops real dependencies
    /// from the graph, alphabetically and silently.
    /// </summary>
    [Fact]
    public void TypeReferences_AreNotSubjectToTheArtifactBound()
    {
        var options = new ArtifactOptions { MaxRefsPerFile = 2 };
        string content = string.Join(
            '\n',
            Enumerable.Range(0, 50).Select(i => $"var x{i} = new Type{i:D3}();"));

        IReadOnlyList<FileReference> refs = Extract(
            File("src/A.cs", FileKind.Source, SourceLanguage.CSharp), content, options);

        Assert.Equal(50, Values(refs, RefKind.Type).Count(v => v.StartsWith("Type", StringComparison.Ordinal)));
    }

    [Fact]
    public void LinkingCanBeTurnedOff()
    {
        var options = new ArtifactOptions { LinkPaths = false, LinkSymbols = false };

        IReadOnlyList<FileReference> refs = Extract(
            File("docs/policy.md", FileKind.Doc, SourceLanguage.Markdown),
            "See src/billing/refund.ts and AuditLog for PAY-142.\n",
            options);

        Assert.Empty(Values(refs, RefKind.Path));
        Assert.Empty(Values(refs, RefKind.Symbol));
        Assert.Equal(["PAY-142"], Values(refs, RefKind.Key));
    }
}
