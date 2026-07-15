using RepoContext.Core.Context;

namespace RepoContext.Core.Tests.Context;

/// <summary>The lossy comment/blank stripper behind <c>--strip-comments</c> (ADR 0012).</summary>
public class CommentStripperTests
{
    [Fact]
    public void NoComments_ReturnsUnchanged()
    {
        const string code = "const a = 1;\nconst b = 2;";

        (string text, bool changed) = CommentStripper.Strip(code);

        Assert.False(changed);
        Assert.Equal(code, text);
    }

    [Fact]
    public void FullLineLineComments_AreDropped()
    {
        string code = string.Join('\n', "// header", "const a = 1;", "  // indented note", "const b = 2;");

        (string text, bool changed) = CommentStripper.Strip(code);

        Assert.True(changed);
        Assert.Equal("const a = 1;\nconst b = 2;", text);
    }

    [Fact]
    public void MixedCodeAndTrailingComment_IsKeptIntact()
    {
        const string code = "const a = 1; // keep this whole line";

        (string text, bool changed) = CommentStripper.Strip(code);

        Assert.False(changed);
        Assert.Equal(code, text);
    }

    [Fact]
    public void MultiLineBlockComment_IsDropped()
    {
        string code = string.Join('\n',
            "/**", " * A doc block.", " * @param x the thing", " */", "function f(x) {}");

        (string text, bool changed) = CommentStripper.Strip(code);

        Assert.True(changed);
        Assert.Equal("function f(x) {}", text);
    }

    [Fact]
    public void SingleLineBlockComment_IsDropped()
    {
        string code = string.Join('\n', "/* banner */", "const a = 1;");

        (string text, bool changed) = CommentStripper.Strip(code);

        Assert.True(changed);
        Assert.Equal("const a = 1;", text);
    }

    [Fact]
    public void BlankRuns_AreCollapsed()
    {
        string code = string.Join('\n', "const a = 1;", "", "", "", "const b = 2;");

        (string text, bool changed) = CommentStripper.Strip(code);

        Assert.True(changed);
        Assert.Equal("const a = 1;\n\nconst b = 2;", text);
    }

    [Fact]
    public void CodeAfterBlockCommentClose_IsPreserved()
    {
        string code = string.Join('\n', "/* lead", "   more */ const a = 1;", "const b = 2;");

        (string text, bool changed) = CommentStripper.Strip(code);

        Assert.True(changed);
        Assert.Equal("const a = 1;\nconst b = 2;", text);
    }

    [Fact]
    public void NeverDropsCode_OnlyReducesOrKeeps()
    {
        // Property: every non-comment code token in the input survives.
        string code = string.Join('\n',
            "// c1", "export function loginUser(x) {", "  return x; // trailing", "}", "/* tail */");

        (string text, _) = CommentStripper.Strip(code);

        Assert.Contains("export function loginUser(x) {", text);
        Assert.Contains("return x; // trailing", text);
        Assert.Contains("}", text);
        Assert.DoesNotContain("// c1", text);
        Assert.DoesNotContain("/* tail */", text);
    }
}
