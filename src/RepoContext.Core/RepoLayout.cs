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
    /// Makes <paramref name="path"/> ready to be written: it must lie in the
    /// index directory with no symbolic link from <c>.repoctx/</c> down to it,
    /// and its parent directory is created.
    /// </summary>
    /// <exception cref="UnsafePathException">A link is on the way.</exception>
    /// <remarks>
    /// Every store under <c>.repoctx/</c> goes through here before it writes.
    /// The directory is normally git-ignored, but a hostile checkout can commit
    /// one - and a link in it would otherwise redirect index, ledger, session or
    /// dashboard writes anywhere on the machine.
    /// </remarks>
    public void PrepareIndexFile(string path)
    {
        SafePaths.EnsureNoLinks(IndexDirectory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>
    /// Converts a user-supplied path (absolute, or relative to
    /// <paramref name="currentDirectory"/>) into a repo-relative path with
    /// <c>/</c> separators. Returns null if it falls outside the repository.
    /// </summary>
    /// <remarks>
    /// Two spellings can name the same location. macOS reaches <c>/var</c> and
    /// <c>/tmp</c> through links to <c>/private/…</c>, and a repository checked
    /// out under a symlinked directory is reached both ways routinely — so a
    /// path a caller supplies and the root a command discovered can point at the
    /// same file and still differ as strings. Comparing them literally reports
    /// "outside the repository" for a file plainly inside it.
    /// <para>
    /// The link-resolving comparison is only a fallback for when the literal one
    /// says no, so it can turn a wrong rejection into an answer and can never
    /// change a path that already resolved. A symlink that genuinely leaves the
    /// repository still resolves to somewhere outside the resolved root and is
    /// still rejected.
    /// </para>
    /// </remarks>
    public string? ToRelativePath(string input, string currentDirectory)
    {
        string full = Path.GetFullPath(Path.Combine(currentDirectory, input));
        return Relative(Root, full) ?? Relative(ResolveLinks(Root), ResolveLinks(full));
    }

    private static string? Relative(string root, string full)
    {
        string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        // A path on another Windows drive has no relative form at all and comes
        // back absolute - outside, not a strangely spelled repository path.
        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".."
            || Path.IsPathRooted(relative)
            ? null
            : relative;
    }

    /// <summary>
    /// Resolves symlinked components of <paramref name="path"/>, walking down
    /// from the filesystem root so a linked <i>ancestor</i> is resolved too.
    /// </summary>
    /// <remarks>
    /// Best-effort by design: a component that cannot be inspected is kept
    /// verbatim, because this only ever feeds a second comparison attempt that
    /// would otherwise have been a rejection.
    /// </remarks>
    private static string ResolveLinks(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        foreach (string part in full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                FileSystemInfo? entry = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current) ? new FileInfo(current) : null;
                if (entry?.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
            }
        }

        return current;
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
