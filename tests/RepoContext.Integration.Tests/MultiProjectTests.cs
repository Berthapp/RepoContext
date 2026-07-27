namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end coverage for a root that holds several projects: initializing at
/// the top must index every project and every subfolder below it.
/// </summary>
public class MultiProjectTests
{
    [Fact]
    public void Init_ReportsScopeAndEveryDetectedProject()
    {
        using var ws = new FixtureWorkspace("multi-project");

        CliResult init = ws.Run("init");

        Assert.Equal(0, init.ExitCode);
        Assert.Contains("scope: the whole repository", init.StdOut);
        Assert.Contains("projects: 2 detected", init.StdOut);
        Assert.Contains("apps/web (node, package.json)", init.StdOut);
        Assert.Contains("services/api (dotnet, SampleApi.csproj)", init.StdOut);
    }

    [Theory]
    [InlineData("apps/web/src/auth/login.ts")]
    [InlineData("apps/web/src/lib/session.ts")]
    [InlineData("apps/web/package.json")]
    [InlineData("services/api/Controllers/HealthController.cs")]
    [InlineData("services/api/Services/TokenService.cs")]
    [InlineData("services/api/SampleApi.csproj")]
    [InlineData("tools/scripts/deploy.js")]
    public void Index_CoversEveryProjectAndSubfolder(string path)
    {
        using var ws = Indexed();

        Assert.Equal(0, ws.Run("outline", path).ExitCode);
    }

    [Theory]
    [InlineData("apps/web/dist/bundle.js")]      // default exclude, nested project
    [InlineData("apps/web/generated/schema.ts")] // nested .repoctxignore
    public void Index_SkipsGeneratedOutputInsideNestedProjects(string path)
    {
        using var ws = Indexed();

        CliResult outline = ws.Run("outline", path);

        Assert.Equal(1, outline.ExitCode);
        Assert.Contains("File not found in index", outline.StdErr);
    }

    [Fact]
    public void Search_FindsSymbolsInEveryProject()
    {
        using var ws = Indexed();

        CliResult web = ws.Run("search", "loginWebUser", "--symbols", "--format", "json");
        CliResult api = ws.Run("search", "CanIssue", "--symbols", "--format", "json");

        Assert.Equal(0, web.ExitCode);
        Assert.Equal(0, api.ExitCode);
        Assert.Contains("apps/web/src/auth/login.ts", web.StdOut);
        Assert.Contains("services/api/Services/TokenService.cs", api.StdOut);
    }

    [Fact]
    public void Context_ReachesAcrossProjectBoundaries()
    {
        using var ws = Indexed();

        CliResult api = ws.Run("context", "issue api tokens", "--format", "json");
        CliResult web = ws.Run("context", "log the user into the web app", "--format", "json");

        Assert.Equal(0, api.ExitCode);
        Assert.Equal(0, web.ExitCode);
        Assert.Contains("services/api", api.StdOut);
        Assert.Contains("apps/web", web.StdOut);
    }

    [Fact]
    public void Index_WarnsWhenConfiguredIncludeRootsMissTheProjects()
    {
        using var ws = new FixtureWorkspace("multi-project");
        ws.Run("init");

        // A configuration written before 0.8.0: root-anchored include list.
        string configPath = ws.PathOf("repoctx.config.json");
        string config = File.ReadAllText(configPath);
        File.WriteAllText(
            configPath,
            config.Replace("\"include\": []", "\"include\": [\"src\", \"app\", \"lib\", \"docs\"]",
                StringComparison.Ordinal));

        CliResult index = ws.Run("index");

        // Still a success - the warning explains the empty result, it is not an error.
        Assert.Equal(0, index.ExitCode);
        Assert.Contains("files: 0", index.StdOut);
        Assert.Contains("include root(s) do not exist", index.StdErr);
        Assert.Contains("set it to []", index.StdErr);
    }

    [Fact]
    public void Index_IsQuietWhenTheDefaultScopeIsUsed()
    {
        using var ws = new FixtureWorkspace("multi-project");
        ws.Run("init");

        CliResult index = ws.Run("index");

        Assert.Equal(0, index.ExitCode);
        Assert.Equal(string.Empty, index.StdErr);
    }

    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("multi-project");
        ws.Run("init");
        ws.Run("index");
        return ws;
    }
}
