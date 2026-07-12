using RepoContext.Core.Indexing;

namespace RepoContext.Core.Tests.Indexing;

public class TokensTests
{
    [Fact]
    public void Count_IsDeterministic_AndOffline()
    {
        const string code = "export async function loginUser(credentials: Credentials): Promise<Session> {}";

        int first = Tokens.Count(code);
        int second = Tokens.Count(code);

        Assert.Equal(first, second);
        Assert.InRange(first, 1, code.Length);
    }

    [Fact]
    public void Count_OfEmptyText_IsZero()
    {
        Assert.Equal(0, Tokens.Count(string.Empty));
    }

    [Fact]
    public void Count_BeatsTheBytesHeuristic_OnWhitespaceHeavyText()
    {
        // Indented code is the norm in repositories; a BPE folds runs of
        // spaces into single tokens where bytes/4 overcounts them.
        string indented = string.Concat(Enumerable.Repeat("        return value;\n", 50));

        Assert.True(Tokens.Count(indented) < indented.Length / 4);
    }
}
