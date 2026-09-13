using RepoContext.Core.Guard;

namespace RepoContext.Core.Tests.Guard;

/// <summary>
/// The documented shell subset of the read-cost guard (ADR 0023).
/// </summary>
/// <remarks>
/// The asymmetry under test: missing a read costs tokens, misreading a command
/// costs the user their work. Everything outside the subset must therefore come
/// back as <see cref="ShellReadKind.Unsupported"/>, never as a guess.
/// </remarks>
public class ShellReadParserTests
{
    [Theory]
    [InlineData("cat src/app.ts", "src/app.ts", null)]
    [InlineData("head -n 40 src/app.ts", "src/app.ts", 40)]
    [InlineData("head -n40 src/app.ts", "src/app.ts", 40)]
    [InlineData("tail -n 5 src/app.ts", "src/app.ts", 5)]
    [InlineData("head src/app.ts", "src/app.ts", 10)]
    [InlineData("nl src/app.ts", "src/app.ts", null)]
    [InlineData("less src/app.ts", "src/app.ts", null)]
    [InlineData("/usr/bin/cat src/app.ts", "src/app.ts", null)]
    [InlineData("cat -- src/app.ts", "src/app.ts", null)]
    public void Parse_RecognizesTheSupportedReaders(string command, string path, int? limit)
    {
        ShellReadParse parse = ShellReadParser.Parse(command);

        Assert.Equal(ShellReadKind.Read, parse.Kind);
        GuardReadRequest read = Assert.Single(parse.Reads);
        Assert.Equal(path, read.Path);
        Assert.Equal(limit, read.LineLimit);
    }

    [Fact]
    public void Parse_ReadsEveryInputFile()
    {
        ShellReadParse parse = ShellReadParser.Parse("cat a.ts b.ts c.ts");

        Assert.Equal(ShellReadKind.Read, parse.Kind);
        Assert.Equal<string[]>(["a.ts", "b.ts", "c.ts"], [.. parse.Reads.Select(r => r.Path)]);
    }

    [Theory]
    [InlineData("cat \"src/my file.ts\"", "src/my file.ts")]
    [InlineData("cat 'src/my file.ts'", "src/my file.ts")]
    [InlineData("cat src/my\\ file.ts", "src/my file.ts")]
    [InlineData("cat \"C:\\\\src\\\\app.ts\"", "C:\\src\\app.ts")]
    [InlineData("cat C:\\src\\app.ts", "C:\\src\\app.ts")]
    [InlineData("cat \"it's.ts\"", "it's.ts")]
    public void Parse_HandlesQuotingAndWindowsPaths(string command, string path)
    {
        ShellReadParse parse = ShellReadParser.Parse(command);

        Assert.Equal(ShellReadKind.Read, parse.Kind);
        Assert.Equal(path, Assert.Single(parse.Reads).Path);
    }

    [Fact]
    public void Parse_ModelsTheBoundedSedForm()
    {
        ShellReadParse parse = ShellReadParser.Parse("sed -n '120,180p' src/app.ts");

        Assert.Equal(ShellReadKind.Read, parse.Kind);
        GuardReadRequest read = Assert.Single(parse.Reads);
        Assert.Equal(120, read.StartLine);
        Assert.Equal(61, read.LineLimit);
    }

    [Theory]
    [InlineData("cat src/*.ts")]
    [InlineData("cat a.ts | head -n 20")]
    [InlineData("cat a.ts > b.ts")]
    [InlineData("cat $FILE")]
    [InlineData("cat \"$(ls)\"")]
    [InlineData("cat a.ts && cat b.ts")]
    [InlineData("cat a.ts; cat b.ts")]
    [InlineData("cat {a,b}.ts")]
    [InlineData("bash script.sh")]
    [InlineData("python3 read_everything.py")]
    [InlineData("head -c 4000 a.ts")]
    [InlineData("bat --line-range 10:20 a.ts")]
    [InlineData("sed -i 's/a/b/' a.ts")]
    [InlineData("sed -n '1~2p' a.ts")]
    [InlineData("cat 'unbalanced")]
    public void Parse_RefusesToGuessOutsideTheSubset(string command) =>
        Assert.Equal(ShellReadKind.Unsupported, ShellReadParser.Parse(command).Kind);

    [Theory]
    [InlineData("repoctx outline src/app.ts")]
    [InlineData("repoctx context 'fix the timeout' --detail slices")]
    [InlineData("git status")]
    [InlineData("rg --files src")]
    public void Parse_NeverTreatsTheAlternativeCallAsARead(string command)
    {
        // Blocking the cheaper call the guard itself suggests would be the one
        // failure that makes an agent unable to make progress at all.
        ShellReadParse parse = ShellReadParser.Parse(command);

        Assert.Equal(ShellReadKind.NotARead, parse.Kind);
        Assert.Empty(parse.Reads);
    }

    [Fact]
    public void Parse_TreatsAReaderWithoutAPathAsStandardInput()
    {
        ShellReadParse parse = ShellReadParser.Parse("cat");

        Assert.Equal(ShellReadKind.NotARead, parse.Kind);
    }

    [Fact]
    public void TrySplit_ReportsUnbalancedQuoting()
    {
        Assert.False(ShellReadParser.TrySplit("cat \"a.ts", out List<string> words));
        Assert.Empty(words);
    }
}
