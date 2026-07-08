namespace RepoContext.Core.Tests.TestSupport;

/// <summary>Locates read-only fixture files under tests/fixtures.</summary>
public static class Fixtures
{
    public static string Path(string relative)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(d.FullName, "RepoContext.slnx")))
            {
                return System.IO.Path.Combine(d.FullName, "tests", "fixtures", relative);
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    public static string Read(string relative) => File.ReadAllText(Path(relative));
}
