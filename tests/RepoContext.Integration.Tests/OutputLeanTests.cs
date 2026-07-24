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
        Assert.Contains(items, item => item.GetProperty("path").GetString() == original);
        Assert.True(duplicate.Value.TryGetProperty("receipt", out JsonElement receipt));
        Assert.False(string.IsNullOrWhiteSpace(receipt.GetString()));
        Assert.Equal(0, duplicate.Value.GetProperty("estimated_tokens").GetInt32());
        Assert.Equal(0, duplicate.Value.GetProperty("content_tokens").GetInt32());
        Assert.Equal(0, duplicate.Value.GetProperty("projected_read_tokens").GetInt32());
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

        CliResult plainResult = ws.Run(args);
        int plain = SliceCharTotal(plainResult.StdOut);
        CliResult strippedResult = ws.Run([.. args, "--strip-comments"]);
        int stripped = SliceCharTotal(strippedResult.StdOut);

        Assert.True(stripped <= plain, $"stripped {stripped} should be <= plain {plain}");
        // The fixture has comment lines, so at least one slice must shrink and flag.
        using JsonDocument doc = JsonDocument.Parse(strippedResult.StdOut);
        JsonElement strippedItem = doc.RootElement.GetProperty("results").EnumerateArray()
            .First(i => i.TryGetProperty("stripped", out JsonElement s) && s.GetBoolean());

        // A receipt proves possession of the text actually delivered. The same
        // source range therefore has a different receipt after a lossy strip.
        using JsonDocument plainDoc = JsonDocument.Parse(plainResult.StdOut);
        JsonElement plainItem = plainDoc.RootElement.GetProperty("results").EnumerateArray()
            .First(i => i.GetProperty("path").GetString()
                == strippedItem.GetProperty("path").GetString());
        JsonElement? changedPlain = null;
        JsonElement? changedStripped = null;
        foreach (JsonElement strippedSpan in strippedItem.GetProperty("spans").EnumerateArray())
        {
            JsonElement? plainSpan = plainItem.GetProperty("spans").EnumerateArray()
                .Where(span =>
                    span.GetProperty("start_line").GetInt32()
                        == strippedSpan.GetProperty("start_line").GetInt32()
                    && span.GetProperty("end_line").GetInt32()
                        == strippedSpan.GetProperty("end_line").GetInt32())
                .Cast<JsonElement?>()
                .FirstOrDefault();
            if (plainSpan is not null
                && plainSpan.Value.GetProperty("text").GetString()
                    != strippedSpan.GetProperty("text").GetString())
            {
                changedPlain = plainSpan;
                changedStripped = strippedSpan;
                break;
            }
        }

        Assert.NotNull(changedPlain);
        Assert.NotNull(changedStripped);
        Assert.NotEqual(
            changedPlain.Value.GetProperty("text").GetString(),
            changedStripped.Value.GetProperty("text").GetString());
        Assert.NotEqual(
            changedPlain.Value.GetProperty("receipt").GetString(),
            changedStripped.Value.GetProperty("receipt").GetString());
    }

    private static int SliceCharTotal(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("results").EnumerateArray()
            .Sum(item => item.TryGetProperty("spans", out JsonElement spans)
                ? spans.EnumerateArray().Sum(span => span.GetProperty("text").GetString()!.Length)
                : item.TryGetProperty("snippet", out JsonElement snippet)
                    ? snippet.GetString()!.Length
                    : 0);
    }
}
