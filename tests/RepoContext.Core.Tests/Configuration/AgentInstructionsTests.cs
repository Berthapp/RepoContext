using RepoContext.Core.Configuration;

namespace RepoContext.Core.Tests.Configuration;

public sealed class AgentInstructionsTests : IDisposable
{
    // Mirror the (internal) markers so the tests exercise the public behaviour
    // as a black box; if these ever drift, the idempotency assertions fail.
    private const string BeginMarker = "<!-- BEGIN RepoContext (managed by `repoctx init`) -->";
    private const string EndMarker = "<!-- END RepoContext -->";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "repoctx-agents", Guid.NewGuid().ToString("N"));

    public AgentInstructionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Ensure_CreatesFile_WhenMissing()
    {
        AgentFileResult result = AgentInstructions.Ensure(_root, "CLAUDE.md");

        Assert.Equal(AgentFileChange.Created, result.Change);
        string content = File.ReadAllText(Path.Combine(_root, "CLAUDE.md"));
        Assert.Contains(BeginMarker, content);
        Assert.Contains("repoctx context", content);
        Assert.Contains(EndMarker, content);
    }

    [Fact]
    public void Ensure_IsIdempotent_OnRepeatedRuns()
    {
        AgentInstructions.Ensure(_root, "CLAUDE.md");
        string first = File.ReadAllText(Path.Combine(_root, "CLAUDE.md"));

        AgentFileResult second = AgentInstructions.Ensure(_root, "CLAUDE.md");

        Assert.Equal(AgentFileChange.Unchanged, second.Change);
        Assert.Equal(first, File.ReadAllText(Path.Combine(_root, "CLAUDE.md")));
    }

    [Fact]
    public void Ensure_AppendsBlock_PreservingExistingContent()
    {
        string path = Path.Combine(_root, "AGENTS.md");
        File.WriteAllText(path, "# My project\n\nExisting guidance.\n");

        AgentFileResult result = AgentInstructions.Ensure(_root, "AGENTS.md");

        Assert.Equal(AgentFileChange.Updated, result.Change);
        string content = File.ReadAllText(path);
        Assert.StartsWith("# My project\n\nExisting guidance.\n", content);
        Assert.Contains(BeginMarker, content);
        // Appending once then again does not duplicate the managed block.
        AgentInstructions.Ensure(_root, "AGENTS.md");
        string after = File.ReadAllText(path);
        Assert.Equal(1, CountOccurrences(after, BeginMarker));
    }

    [Fact]
    public void Ensure_ReplacesStaleBlock_InPlace()
    {
        string path = Path.Combine(_root, "CLAUDE.md");
        string stale = "# Header\n\n" + BeginMarker + "\nold text\n"
            + EndMarker + "\n\n# Footer\n";
        File.WriteAllText(path, stale);

        AgentFileResult result = AgentInstructions.Ensure(_root, "CLAUDE.md");

        Assert.Equal(AgentFileChange.Updated, result.Change);
        string content = File.ReadAllText(path);
        Assert.StartsWith("# Header\n\n", content);
        Assert.EndsWith("\n\n# Footer\n", content);
        Assert.Contains("repoctx context", content);
        Assert.DoesNotContain("old text", content);
        Assert.Equal(1, CountOccurrences(content, BeginMarker));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
