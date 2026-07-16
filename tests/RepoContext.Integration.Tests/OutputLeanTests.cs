using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for the output-lean measures (ADR 0012): duplicate-content
/// dedup, the JSON-escape-tax avoidance of md slices, and --strip-comments.
/// </summary>
public class OutputLeanTests
{
    [Fact]
    public void DuplicateContent_IsMarkedAndChargedZero()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        // A byte-identical copy of a matching file: same content hash, both rank
        // for "login", so the second is a duplicate of the first.
        File.Copy(ws.PathOf("src/auth/login.ts"), ws.PathOf("src/auth/login_copy.ts"));
        ws.Run("index");

        CliResult result = ws.Run("context", "login", "--detail", "slices",
            "--top", "8", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        var items = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
        JsonElement? duplicate = items
            .Where(i => i.TryGetProperty("duplicate_of", out _))
            .Cast<JsonElement?>()
            .FirstOrDefault();

        Assert.NotNull(duplicate);
        string dupPath = duplicate.Value.GetProperty("path").GetString()!;
        string original = duplicate.Value.GetProperty("duplicate_of").GetString()!;
        Assert.Contains(dupPath, new[] { "src/auth/login.ts", "src/auth/login_copy.ts" });
        Assert.Contains(original, new[] { "src/auth/login.ts", "src/auth/login_copy.ts" });
        Assert.NotEqual(dupPath, original);
        Assert.Equal(0, duplicate.Value.GetProperty("estimated_tokens").GetInt32());
        Assert.False(duplicate.Value.TryGetProperty("snippet", out _));
    }

    [Fact]
    public void MdSlices_ChargeLessThanJson_NoEscapeTax()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        string[] args = ["context", "change the login logic", "--detail", "slices", "--top", "4"];

        CliResult json = ws.Run([.. args, "--format", "json"]);
        int jsonTotal = JsonDocument.Parse(json.StdOut).RootElement
            .GetProperty("estimated_tokens").GetInt32();

        CliResult md = ws.Run([.. args, "--format", "md"]);
        Match m = Regex.Match(md.StdOut, @"~(\d+) estimated tokens");
        Assert.True(m.Success, md.StdOut);
        int mdTotal = int.Parse(m.Groups[1].Value);

        // Same bundle, but md delivers raw slice text while JSON pays the
        // escaping tax — so md is charged strictly less.
        Assert.True(mdTotal < jsonTotal, $"md {mdTotal} should be < json {jsonTotal}");
    }

    [Fact]
    public void StripComments_ShrinksSlices_AndFlagsApproximateLines()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        string[] args = ["context", "change the login logic", "--detail", "slices",
            "--top", "4", "--format", "json"];

        int plain = SliceCharTotal(ws.Run(args).StdOut);
        CliResult strippedResult = ws.Run([.. args, "--strip-comments"]);
        int stripped = SliceCharTotal(strippedResult.StdOut);

        Assert.True(stripped <= plain, $"stripped {stripped} should be <= plain {plain}");
        // The fixture has comment lines, so at least one slice must shrink and flag.
        using JsonDocument doc = JsonDocument.Parse(strippedResult.StdOut);
        Assert.Contains(doc.RootElement.GetProperty("results").EnumerateArray(),
            i => i.TryGetProperty("stripped", out JsonElement s) && s.GetBoolean());
    }

    private static int SliceCharTotal(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("results").EnumerateArray()
            .Where(i => i.TryGetProperty("snippet", out _))
            .Sum(i => i.GetProperty("snippet").GetString()!.Length);
    }
}
