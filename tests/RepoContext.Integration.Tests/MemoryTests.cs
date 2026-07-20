using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for the M9 agent memory (ADR 0013): the add/search/rm
/// round-trip, context folding with byte-identical repeat runs, session
/// scoping, staleness after an edit, and the argument contract.
/// </summary>
public class MemoryTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");
        return ws;
    }

    [Fact]
    public void AddSearchRemove_RoundTrips()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult add = ws.Run("memory", "add",
            "JWT chosen over cookie sessions: mobile clients cannot hold cookies.",
            "--kind", "decision", "--file", "src/auth/login.ts", "--tag", "auth",
            "--format", "json");
        Assert.Equal(0, add.ExitCode);
        using JsonDocument addDoc = JsonDocument.Parse(add.StdOut);
        JsonElement entry = addDoc.RootElement.GetProperty("entry");
        string id = entry.GetProperty("id").GetString()!;
        Assert.Equal(2, addDoc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("decision", entry.GetProperty("kind").GetString());
        Assert.Equal(1, addDoc.RootElement.GetProperty("total_entries").GetInt32());
        Assert.True(entry.GetProperty("files").TryGetProperty("src/auth/login.ts", out JsonElement hash));
        Assert.Equal(12, hash.GetString()!.Length);

        CliResult search = ws.Run("memory", "search", "jwt auth", "--format", "json");
        Assert.Equal(0, search.ExitCode);
        using JsonDocument searchDoc = JsonDocument.Parse(search.StdOut);
        JsonElement hit = Assert.Single(
            searchDoc.RootElement.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal(id, hit.GetProperty("id").GetString());
        Assert.False(hit.TryGetProperty("stale", out _));
        var reasons = hit.GetProperty("reasons").EnumerateArray()
            .Select(r => r.GetString()).ToList();
        Assert.Contains("term:jwt", reasons);
        Assert.Contains("tag:auth", reasons);
        Assert.True(hit.GetProperty("estimated_tokens").GetInt32() > 0);

        CliResult rm = ws.Run("memory", "rm", id, "--format", "json");
        Assert.Equal(0, rm.ExitCode);
        CliResult after = ws.Run("memory", "search", "jwt", "--format", "json");
        using JsonDocument afterDoc = JsonDocument.Parse(after.StdOut);
        Assert.Equal(0, afterDoc.RootElement.GetProperty("count").GetInt32());

        Assert.Equal(1, ws.Run("memory", "rm", id).ExitCode);
    }

    [Fact]
    public void Context_FoldsMemoryIn_AndRepeatRunsAreByteIdentical()
    {
        using FixtureWorkspace ws = Indexed();
        Assert.Equal(0, ws.Run("memory", "add",
            "Login validates credentials against the session store.",
            "--file", "src/auth/login.ts").ExitCode);

        string[] args = ["context", "change the login logic", "--top", "4",
            "--budget-tokens", "2000", "--format", "json"];
        CliResult first = ws.Run(args);
        CliResult second = ws.Run(args);

        Assert.Equal(0, first.ExitCode);
        // Determinism: identical index + store + query ⇒ identical bytes.
        Assert.Equal(first.StdOut, second.StdOut);

        using JsonDocument doc = JsonDocument.Parse(first.StdOut);
        JsonElement memory = Assert.Single(
            doc.RootElement.GetProperty("memories").EnumerateArray().ToList());
        Assert.Contains("linked:src/auth/login.ts",
            memory.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.True(memory.GetProperty("estimated_tokens").GetInt32() > 0);

        // The bundle total includes the memory charge.
        int itemTokens = doc.RootElement.GetProperty("results").EnumerateArray()
            .Sum(i => i.GetProperty("estimated_tokens").GetInt32());
        int memoryTokens = memory.GetProperty("estimated_tokens").GetInt32();
        Assert.Equal(itemTokens + memoryTokens,
            doc.RootElement.GetProperty("estimated_tokens").GetInt32());

        // Text format shows the memory section; --no-memory removes it.
        CliResult text = ws.Run("context", "change the login logic", "--top", "4");
        Assert.Contains("Memory:", text.StdOut);
        CliResult noMemory = ws.Run("context", "change the login logic", "--top", "4", "--no-memory");
        Assert.DoesNotContain("Memory:", noMemory.StdOut);
        using JsonDocument noMemoryDoc = JsonDocument.Parse(
            ws.Run("context", "change the login logic", "--top", "4", "--no-memory",
                "--format", "json").StdOut);
        Assert.False(noMemoryDoc.RootElement.TryGetProperty("memories", out _));
    }

    [Fact]
    public void SessionScopedMemory_IsInvisible_WithoutItsSession()
    {
        using FixtureWorkspace ws = Indexed();
        Assert.Equal(0, ws.Run("memory", "add", "Working on the login retry bug.",
            "--session", "fix-1").ExitCode);

        using JsonDocument without = JsonDocument.Parse(
            ws.Run("memory", "search", "login", "--format", "json").StdOut);
        Assert.Equal(0, without.RootElement.GetProperty("count").GetInt32());

        using JsonDocument with = JsonDocument.Parse(
            ws.Run("memory", "search", "login", "--session", "fix-1", "--format", "json").StdOut);
        Assert.Equal(1, with.RootElement.GetProperty("count").GetInt32());

        // context: the scratchpad only rides along inside its session.
        using JsonDocument ctxOther = JsonDocument.Parse(
            ws.Run("context", "login retry", "--format", "json").StdOut);
        Assert.False(ctxOther.RootElement.TryGetProperty("memories", out _));
        using JsonDocument ctxSame = JsonDocument.Parse(
            ws.Run("context", "login retry", "--session", "fix-1", "--format", "json").StdOut);
        Assert.True(ctxSame.RootElement.TryGetProperty("memories", out JsonElement memories));
        Assert.Equal(1, memories.GetArrayLength());
    }

    [Fact]
    public void EditedLinkedFile_MakesMemoryStale_UntilReAdded()
    {
        using FixtureWorkspace ws = Indexed();
        Assert.Equal(0, ws.Run("memory", "add", "Login validates credentials.",
            "--file", "src/auth/login.ts", "--format", "json").ExitCode);

        string path = ws.PathOf("src/auth/login.ts");
        File.WriteAllText(path, File.ReadAllText(path) + "\n// drift\n");
        ws.Run("index");

        using JsonDocument doc = JsonDocument.Parse(
            ws.Run("memory", "search", "login", "--format", "json").StdOut);
        JsonElement hit = Assert.Single(
            doc.RootElement.GetProperty("results").EnumerateArray().ToList());
        Assert.True(hit.GetProperty("stale").GetBoolean());
        Assert.Equal("src/auth/login.ts",
            Assert.Single(hit.GetProperty("stale_files").EnumerateArray().ToList()).GetString());

        // Re-adding the same text refreshes the hash link: stale clears.
        CliResult readd = ws.Run("memory", "add", "Login validates credentials.",
            "--file", "src/auth/login.ts", "--format", "json");
        using JsonDocument readdDoc = JsonDocument.Parse(readd.StdOut);
        Assert.True(readdDoc.RootElement.GetProperty("updated").GetBoolean());
        using JsonDocument fresh = JsonDocument.Parse(
            ws.Run("memory", "search", "login", "--format", "json").StdOut);
        Assert.False(Assert.Single(fresh.RootElement.GetProperty("results").EnumerateArray().ToList())
            .TryGetProperty("stale", out _));
    }

    [Fact]
    public void ArgumentContract_InvalidInputsAreRejected()
    {
        using FixtureWorkspace ws = Indexed();

        Assert.Equal(3, ws.Run("memory", "add", "x", "--kind", "wisdom").ExitCode);
        Assert.Equal(3, ws.Run("memory", "add", "x", "--tag", "No Spaces!").ExitCode);
        Assert.Equal(3, ws.Run("memory", "add", new string('x', 2001)).ExitCode);
        Assert.Equal(3, ws.Run("memory", "add", "x", "--session", "no/slashes").ExitCode);
        Assert.Equal(3, ws.Run("memory", "search", "x", "--top", "0").ExitCode);
        Assert.Equal(1, ws.Run("memory", "add", "x", "--file", "does/not/exist.ts").ExitCode);

        var noIndex = new FixtureWorkspace("sample-ts");
        try
        {
            noIndex.Run("init");
            Assert.Equal(2, noIndex.Run("memory", "add", "x").ExitCode);
            Assert.Equal(2, noIndex.Run("memory", "search", "x").ExitCode);
        }
        finally
        {
            noIndex.Dispose();
        }
    }
}
