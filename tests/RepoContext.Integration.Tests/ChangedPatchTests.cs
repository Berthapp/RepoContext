using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for <c>changed --patch</c> (ADR 0012): after an edit, the
/// response carries delta hunks whose token cost undercuts the full re-read.
/// </summary>
public class ChangedPatchTests
{
    [Fact]
    public void Patch_AfterSmallEdit_CarriesHunksCheaperThanFullRead()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        string sessionPath = ws.PathOf("src/auth/session.ts");
        File.WriteAllText(sessionPath, File.ReadAllText(sessionPath) + "\n// patched line\n");

        CliResult result = ws.Run("changed", "--patch", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.True(doc.RootElement.GetProperty("stale").GetBoolean());
        JsonElement modified = doc.RootElement.GetProperty("changed").EnumerateArray()
            .Single(c => c.GetProperty("path").GetString() == "src/auth/session.ts");

        Assert.Equal("modified", modified.GetProperty("status").GetString());
        JsonElement hunks = modified.GetProperty("hunks");
        Assert.True(hunks.GetArrayLength() >= 1);
        Assert.Contains("+// patched line", hunks[0].GetProperty("text").GetString());

        int patchTokens = modified.GetProperty("patch_tokens").GetInt32();
        int fileTokens = modified.GetProperty("file_tokens").GetInt32();
        Assert.InRange(patchTokens, 1, fileTokens - 1);
    }

    [Fact]
    public void WithoutPatchFlag_NoHunksAreEmitted()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        string sessionPath = ws.PathOf("src/auth/session.ts");
        File.WriteAllText(sessionPath, File.ReadAllText(sessionPath) + "\n// edited\n");

        CliResult result = ws.Run("changed", "--format", "json");
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        JsonElement modified = doc.RootElement.GetProperty("changed").EnumerateArray()
            .Single(c => c.GetProperty("path").GetString() == "src/auth/session.ts");

        Assert.False(modified.TryGetProperty("hunks", out _));
        Assert.False(modified.TryGetProperty("patch_tokens", out _));
    }

    [Fact]
    public void Patch_MidFileEdit_AnchorsHunkAtTheEditedLines()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        string loginPath = ws.PathOf("src/auth/login.ts");
        string[] lines = File.ReadAllLines(loginPath);
        int target = lines.Length / 2;
        lines[target] = lines[target] + " // mid-file edit";
        File.WriteAllLines(loginPath, lines);

        CliResult result = ws.Run("changed", "--patch", "--format", "json");
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        JsonElement modified = doc.RootElement.GetProperty("changed").EnumerateArray()
            .Single(c => c.GetProperty("path").GetString() == "src/auth/login.ts");

        JsonElement hunk = modified.GetProperty("hunks")[0];
        int oldStart = hunk.GetProperty("old_start").GetInt32();
        // The hunk starts at most ContextLines above the edited 1-based line.
        Assert.InRange(oldStart, target + 1 - 2, target + 1);
        Assert.Contains("// mid-file edit", hunk.GetProperty("text").GetString());
    }
}
