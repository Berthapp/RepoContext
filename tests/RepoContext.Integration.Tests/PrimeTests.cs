using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for <c>repoctx prime</c> (ADR 0012): a repository primer
/// meant to sit behind a prompt-cache breakpoint, so its one hard guarantee
/// is byte-stability — unrelated edits must not move a single byte.
/// </summary>
public class PrimeTests
{
    [Fact]
    public void Prime_IsByteStable_AcrossAnUnrelatedInPlaceEdit()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        CliResult before = ws.Run("prime");
        Assert.Equal(0, before.ExitCode);
        Assert.Contains("Repository primer", before.StdOut);

        // Same line count, same imports - only content bytes move, in a file
        // that can never be a key file (kind 'test', zero dependents) nor an
        // entrypoint. The primer must not move a byte.
        string path = ws.PathOf("src/auth/__tests__/login.test.ts");
        string[] lines = File.ReadAllLines(path);
        int i = Array.FindIndex(lines, l => l.Contains("user@example.com"));
        Assert.True(i >= 0);
        lines[i] = lines[i].Replace("user@example.com", "someone@example.org");
        File.WriteAllLines(path, lines);
        ws.Run("index");

        CliResult after = ws.Run("prime");
        Assert.Equal(0, after.ExitCode);
        Assert.Equal(before.StdOut, after.StdOut);
    }

    [Fact]
    public void Prime_Json_CarriesQuantizedAggregatesAndKeyFileOutlines()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        CliResult result = ws.Run("prime", "--files", "3", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        JsonElement root = doc.RootElement;
        Assert.Equal("prime", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("approx_files").GetInt32() > 0);
        Assert.True(root.GetProperty("approx_loc").GetInt32() > 0);

        var languages = root.GetProperty("languages").EnumerateArray()
            .Select(l => l.GetProperty("language").GetString()!)
            .ToList();
        Assert.Equal(languages.OrderBy(l => l, StringComparer.Ordinal).ToList(), languages);

        var files = root.GetProperty("results").EnumerateArray().ToList();
        Assert.InRange(files.Count, 1, 3);
        foreach (JsonElement file in files)
        {
            Assert.Equal(12, file.GetProperty("hash").GetString()!.Length);
            Assert.True(file.GetProperty("estimated_tokens").GetInt32() > 0);
        }

        var paths = files.Select(f => f.GetProperty("path").GetString()!).ToList();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToList(), paths);
    }

    [Fact]
    public void Prime_WithoutIndex_FailsWithExitCode2()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");

        Assert.Equal(2, ws.Run("prime").ExitCode);
    }
}
