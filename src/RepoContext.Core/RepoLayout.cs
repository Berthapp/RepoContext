namespace RepoContext.Core;

/// <summary>Resolves the well-known RepoContext paths within a repository.</summary>
public sealed class RepoLayout
{
    private RepoLayout(string root)
    {
        Root = root;
        IndexDirectory = Path.Combine(root, RepoContextInfo.IndexDirectoryName);
        ConfigPath = Path.Combine(root, RepoContextInfo.ConfigFileName);
        DatabasePath = Path.Combine(IndexDirectory, "index.db");
    }

    /// <summary>The repository root (absolute).</summary>
    public string Root { get; }

    /// <summary>The <c>.repoctx/</c> directory.</summary>
    public string IndexDirectory { get; }

    /// <summary>The <c>repoctx.config.json</c> path.</summary>
    public string ConfigPath { get; }

    /// <summary>The SQLite database path.</summary>
    public string DatabasePath { get; }

    /// <summary>Whether an index database exists.</summary>
    public bool HasIndex => File.Exists(DatabasePath);

    /// <summary>Whether the repository has been initialized (config present).</summary>
    public bool IsInitialized => File.Exists(ConfigPath);

    /// <summary>Creates a layout rooted at <paramref name="root"/>.</summary>
    public static RepoLayout For(string root) => new(Path.GetFullPath(root));

    /// <summary>
    /// Converts a user-supplied path (absolute, or relative to
    /// <paramref name="currentDirectory"/>) into a repo-relative path with
    /// <c>/</c> separators. Returns null if it falls outside the repository.
    /// </summary>
    public string? ToRelativePath(string input, string currentDirectory)
    {
        string full = Path.GetFullPath(Path.Combine(currentDirectory, input));
        string relative = Path.GetRelativePath(Root, full).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".."
            ? null
            : relative;
    }

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> to find the nearest
    /// initialized repository (one containing <c>repoctx.config.json</c>).
    /// Returns <c>null</c> if none is found.
    /// </summary>
    public static RepoLayout? Discover(string startDirectory)
    {
        for (DirectoryInfo? dir = new(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, RepoContextInfo.ConfigFileName)))
            {
                return For(dir.FullName);
            }
        }

        return null;
    }
}
