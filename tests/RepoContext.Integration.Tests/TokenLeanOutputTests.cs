using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for the token-lean JSON contract (ADR 0009): every
/// <c>--format json</c> document is a single compact line and null-valued
/// optional fields are omitted, because the JSON format's consumers are AI
/// agents that pay for every token.
/// </summary>
public class TokenLeanOutputTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");
        return ws;
    }

    [Theory]
    [InlineData("search", "login", "--format", "json")]
    [InlineData("context", "change the login logic", "--format", "json")]
    [InlineData("related", "src/auth/login.ts", "--format", "json")]
    [InlineData("architecture", "--format", "json")]
    public void Json_IsACompactSingleLine(params string[] args)
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run(args);
        Assert.Equal(0, result.ExitCode);

        string json = result.StdOut.TrimEnd('\n');
        Assert.DoesNotContain('\n', json);

        // Still a well-formed document of the same shape.
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void SearchJson_OmitsNullHeading()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("search", "login", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        List<JsonElement> hits = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(hits);

        // Headings are only serialized when they carry a value; a hit without
        // one omits the field entirely instead of spending tokens on null.
        Assert.Contains(hits, h => !h.TryGetProperty("heading", out _));
        Assert.All(hits, h =>
        {
            if (h.TryGetProperty("heading", out JsonElement heading))
            {
                Assert.Equal(JsonValueKind.String, heading.ValueKind);
            }
        });
    }
}
