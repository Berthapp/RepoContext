using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for <c>context --session</c> (ADR 0012/0015): delivered
/// evidence is remembered as exact receipts without being promoted to
/// whole-file knowledge, and an edit invalidates only the affected receipts.
/// </summary>
public class SessionTests
{
    [Fact]
    public void Session_SecondCall_ReusesExactUnits_AndEditRevivesThem()
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
        JsonElement firstSliced = firstItems.EnumerateArray()
            .First(i => i.TryGetProperty("spans", out JsonElement spans)
                && spans.GetArrayLength() > 0);
        string rememberedPath = firstSliced.GetProperty("path").GetString()!;

        CliResult second = ws.Run(args);
        Assert.Equal(0, second.ExitCode);
        using JsonDocument secondDoc = JsonDocument.Parse(second.StdOut);
        Assert.True(secondDoc.RootElement.GetProperty("reused_count").GetInt32() > 0);
        Assert.Contains(
            secondDoc.RootElement.GetProperty("reused").EnumerateArray(),
            unit => unit.GetProperty("path").GetString() == rememberedPath
                && unit.TryGetProperty("start_line", out _)
                && unit.TryGetProperty("receipt", out _));
        Assert.DoesNotContain(
            secondDoc.RootElement.GetProperty("results").EnumerateArray(),
            item => item.TryGetProperty("unchanged", out _));

        // Editing a remembered file must bring its content back on call three.
        string edited = ws.PathOf(rememberedPath);
        File.WriteAllText(edited, File.ReadAllText(edited) + "\n// session edit\n");
        ws.Run("index");

        CliResult third = ws.Run(args);
        Assert.Equal(0, third.ExitCode);
        using JsonDocument thirdDoc = JsonDocument.Parse(third.StdOut);
        JsonElement? editedItem = thirdDoc.RootElement.GetProperty("results").EnumerateArray()
            .Where(i => i.GetProperty("path").GetString() == rememberedPath)
            .Cast<JsonElement?>()
            .FirstOrDefault();
        Assert.NotNull(editedItem);
        Assert.True(editedItem.Value.TryGetProperty("spans", out JsonElement revivedSpans));
        Assert.True(revivedSpans.GetArrayLength() > 0);
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
            .First(i => i.TryGetProperty("spans", out JsonElement spans)
                && spans.GetArrayLength() > 0);
        string path = sliced.GetProperty("path").GetString()!;
        string hash = sliced.GetProperty("hash").GetString()!;

        // An explicit matching whole-file assertion takes precedence over the
        // session's narrower span receipts. Its acknowledgement has no range.
        CliResult second = ws.Run("context", "login", "--detail", "slices", "--top", "2",
            "--session", "s2", "--known", $"{path}@{hash}", "--format", "json");
        Assert.Equal(0, second.ExitCode);
        using JsonDocument secondDoc = JsonDocument.Parse(second.StdOut);
        JsonElement acknowledgement = secondDoc.RootElement.GetProperty("reused").EnumerateArray()
            .First(i => i.GetProperty("path").GetString() == path);
        Assert.False(acknowledgement.TryGetProperty("start_line", out _));
        Assert.DoesNotContain(
            secondDoc.RootElement.GetProperty("results").EnumerateArray(),
            item => item.GetProperty("path").GetString() == path);
    }
}
