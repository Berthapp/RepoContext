using System.Text.Json;

namespace RepoContext.Integration.Tests;

public class RelatedDiagnosticsTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("md")]
    public void Related_ReportsUnresolvedImportsEvenWithoutEdges(string format)
    {
        using var ws = new FixtureWorkspace("sample-ts");
        File.WriteAllText(ws.PathOf("src/probe.ts"), "import './missing.js'; import 'external-package';");
        Assert.Equal(0, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("index").ExitCode);

        CliResult result = ws.Run("related", "src/probe.ts", "--format", format);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("./missing.js", result.StdOut);
        Assert.Contains("local-module-not-found", result.StdOut);
        Assert.Contains("unsupported-package-import", result.StdOut);
        if (format == "json")
        {
            using JsonDocument document = JsonDocument.Parse(result.StdOut);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
            JsonElement missing = document.RootElement.GetProperty("unresolved").EnumerateArray()
                .Single(r => r.GetProperty("value").GetString() == "./missing.js");
            Assert.Equal("import", missing.GetProperty("kind").GetString());
        }
    }

    [Theory]
    [InlineData("json", "csharp-syntax")]
    [InlineData("text", "C# syntax inference")]
    [InlineData("md", "C# syntax inference")]
    public void Related_LabelsCSharpInference(string format, string label)
    {
        using var ws = new FixtureWorkspace("sample-cs");
        Assert.Equal(0, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("index").ExitCode);

        CliResult result = ws.Run("related", "Auth/AuthService.cs", "--format", format);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(label, result.StdOut);
    }
}
