namespace RepoContext.Integration.Tests;

/// <summary>A disposable temp copy of a fixture repo for CLI integration tests.</summary>
public sealed class FixtureWorkspace : IDisposable
{
    public FixtureWorkspace(string fixtureName)
    {
        string source = Path.Combine(RepoRoot(), "tests", "fixtures", fixtureName);
        Root = Path.Combine(Path.GetTempPath(), "repoctx-itests", Guid.NewGuid().ToString("N"));
        CopyDirectory(source, Root);
    }

    public string Root { get; }

    public string PathOf(string relative) => System.IO.Path.Combine(Root, relative);

    /// <summary>Runs the CLI with this workspace as the working directory.</summary>
    public CliResult Run(params string[] args) => CliHarness.RunIn(Root, args);

    /// <summary>Runs the CLI with additional environment variables set.</summary>
    public CliResult RunWithEnv(IReadOnlyDictionary<string, string> environment, params string[] args) =>
        CliHarness.RunIn(Root, environment, args);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(d.FullName, "RepoContext.slnx")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination), overwrite: true);
        }
    }
}
