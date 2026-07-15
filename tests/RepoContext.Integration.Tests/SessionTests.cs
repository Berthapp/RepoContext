using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for <c>context --session</c> (ADR 0012): delivered slices
/// are remembered server-side, so the second call returns zero-cost unchanged
/// markers without the caller echoing hashes — and an edit brings content back.
/// </summary>
public class SessionTests
{
    [Fact]
    public void Session_SecondCall_ReturnsUnchangedMarkers_AndEditRevives()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        string[] args = ["context", "login", "--detail", "slices", "--top", "2",
            "--session", "s1", "--format", "json"];

        CliResult first = ws.Run(args);
        Assert.Equal(0, first.ExitCode);
        using JsonDocument firstDoc = JsonDocument.Parse(first.StdOut);
        JsonElement firstItems = firstDoc.RootElement.GetProperty("results");
        Assert.Contains(firstItems.EnumerateArray(), i => i.TryGetProperty("snippet", out _));

        CliResult second = ws.Run(args);
        Assert.Equal(0, second.ExitCode);
        using JsonDocument secondDoc = JsonDocument.Parse(second.StdOut);
        var unchanged = secondDoc.RootElement.GetProperty("results").EnumerateArray()
            .Where(i => i.TryGetProperty("unchanged", out JsonElement u) && u.GetBoolean())
            .Select(i => i.GetProperty("path").GetString())
            .ToList();
        Assert.NotEmpty(unchanged);
        foreach (JsonElement item in secondDoc.RootElement.GetProperty("results").EnumerateArray())
        {
            if (item.TryGetProperty("unchanged", out JsonElement u) && u.GetBoolean())
            {
                Assert.Equal(0, item.GetProperty("estimated_tokens").GetInt32());
                Assert.False(item.TryGetProperty("snippet", out _));
            }
        }

        // Editing a remembered file must bring its content back on call three.
        string edited = ws.PathOf(unchanged[0]!);
        File.WriteAllText(edited, File.ReadAllText(edited) + "\n// session edit\n");
        ws.Run("index");

        CliResult third = ws.Run(args);
        Assert.Equal(0, third.ExitCode);
        using JsonDocument thirdDoc = JsonDocument.Parse(third.StdOut);
        JsonElement? editedItem = thirdDoc.RootElement.GetProperty("results").EnumerateArray()
            .Where(i => i.GetProperty("path").GetString() == unchanged[0])
            .Cast<JsonElement?>()
            .FirstOrDefault();
        Assert.NotNull(editedItem);
        Assert.False(editedItem.Value.TryGetProperty("unchanged", out _));
    }

    [Fact]
    public void Session_InvalidName_IsRejected()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        CliResult result = ws.Run("context", "login", "--session", "no/slashes");
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Session_ExplicitKnown_WinsOverSessionEntry()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        // Seed the session with slices.
        CliResult first = ws.Run("context", "login", "--detail", "slices", "--top", "2",
            "--session", "s2", "--format", "json");
        using JsonDocument firstDoc = JsonDocument.Parse(first.StdOut);
        JsonElement sliced = firstDoc.RootElement.GetProperty("results").EnumerateArray()
            .First(i => i.TryGetProperty("snippet", out _));
        string path = sliced.GetProperty("path").GetString()!;

        // A stale explicit hash overrides the session's fresh one: content returns.
        CliResult second = ws.Run("context", "login", "--detail", "slices", "--top", "2",
            "--session", "s2", "--known", $"{path}@000000000000", "--format", "json");
        using JsonDocument secondDoc = JsonDocument.Parse(second.StdOut);
        JsonElement item = secondDoc.RootElement.GetProperty("results").EnumerateArray()
            .First(i => i.GetProperty("path").GetString() == path);
        Assert.False(item.TryGetProperty("unchanged", out _));
    }
}
