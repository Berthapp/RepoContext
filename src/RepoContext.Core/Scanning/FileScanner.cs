using RepoContext.Core.Configuration;

namespace RepoContext.Core.Scanning;

/// <summary>
/// Walks a repository and selects files for indexing, applying include roots,
/// exclude / <c>.gitignore</c> / <c>.repoctxignore</c> rules, sensitive-file
/// exclusion, binary detection, size limits and kind classification (spec F2).
/// Output is deterministic (ordered by path, Ordinal).
/// </summary>
/// <remarks>
/// The walk descends through every subdirectory of an include root, so a root
/// holding several projects is indexed as a whole. Ignore files are honoured
/// per directory: a nested <c>.gitignore</c> / <c>.repoctxignore</c> applies to
/// its own subtree and overrides rules inherited from parent directories,
/// which is what keeps each project's build output out of the index.
/// </remarks>
public sealed class FileScanner
{
    private const int SniffBytes = 8000;

    private readonly string _repoRoot;
    private readonly RepoctxConfig _config;
    private readonly GitignoreMatcher _sensitive;
    private readonly IgnoreScope _exclude;

    public FileScanner(string repoRoot, RepoctxConfig config)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _sensitive = GitignoreMatcher.FromGlobs(config.SensitiveFiles);
        _exclude = new IgnoreScope(string.Empty, GitignoreMatcher.FromGlobs(config.Exclude));
    }

    /// <summary>Returns whether a repo-relative path is treated as sensitive.</summary>
    public bool IsSensitive(string relativePath) => _sensitive.IsIgnored(relativePath, isDirectory: false);

    /// <summary>
    /// Configured include roots that do not exist on disk, ordered as
    /// configured. A non-empty result means part (or all) of the repository is
    /// silently unindexed - the failure mode of the pre-0.8 default roots in a
    /// repository whose code does not sit in <c>src/app/lib/docs</c>.
    /// </summary>
    public static IReadOnlyList<string> MissingIncludeRoots(string repoRoot, RepoctxConfig config)
    {
        string root = Path.GetFullPath(repoRoot);
        return [.. config.Include.Where(include =>
        {
            string abs = Path.GetFullPath(Path.Combine(root, include));
            return !Directory.Exists(abs) && !File.Exists(abs);
        })];
    }

    /// <summary>Scans the repository and returns the selected files, ordered by path.</summary>
    public IReadOnlyList<ScannedFile> Scan()
    {
        var results = new List<ScannedFile>();
        IReadOnlyList<string> roots = _config.Include.Count > 0 ? _config.Include : ["."];

        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            string abs = Path.GetFullPath(Path.Combine(_repoRoot, root));
            if (Directory.Exists(abs))
            {
                Walk(abs, ScopesDownTo(abs), results, visited);
            }
            else if (File.Exists(abs))
            {
                string? parent = Path.GetDirectoryName(abs);
                TryAddFile(abs, parent is null ? [_exclude] : ScopesDownTo(parent), results, visited);
            }
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return results;
    }

    /// <summary>
    /// Builds the ignore scopes in effect for <paramref name="directory"/>:
    /// the configured excludes plus every ignore file from the repository root
    /// down to (and including) that directory, outermost first.
    /// </summary>
    private List<IgnoreScope> ScopesDownTo(string directory)
    {
        var chain = new List<string>();
        for (DirectoryInfo? dir = new(directory);
             dir is not null && dir.FullName.Length >= _repoRoot.Length;
             dir = dir.Parent)
        {
            chain.Add(dir.FullName);
            if (string.Equals(dir.FullName, _repoRoot, StringComparison.Ordinal))
            {
                break;
            }
        }

        chain.Reverse();

        var scopes = new List<IgnoreScope> { _exclude };
        foreach (string dir in chain)
        {
            AddIgnoreFiles(dir, scopes);
        }

        return scopes;
    }

    private void Walk(string directory, List<IgnoreScope> scopes, List<ScannedFile> results, HashSet<string> visited)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(entries, StringComparer.Ordinal);

        foreach (string entry in entries)
        {
            if (IsSymlink(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                string rel = ToRelative(entry);
                string name = Path.GetFileName(entry);
                if (name is ".git" or ".repoctx")
                {
                    continue;
                }

                // Sensitive directories are pruned entirely - neither the
                // contents nor the paths inside are ever indexed.
                if (_sensitive.IsIgnored(rel, isDirectory: true) || IsIgnored(rel, isDirectory: true, scopes))
                {
                    continue;
                }

                // Ignore files inside the subdirectory only bind that subtree.
                List<IgnoreScope> nested = scopes;
                if (HasIgnoreFile(entry))
                {
                    nested = [.. scopes];
                    AddIgnoreFiles(entry, nested);
                }

                Walk(entry, nested, results, visited);
            }
            else if (File.Exists(entry))
            {
                TryAddFile(entry, scopes, results, visited);
            }
        }
    }

    private void TryAddFile(
        string absolutePath,
        List<IgnoreScope> scopes,
        List<ScannedFile> results,
        HashSet<string> visited)
    {
        if (IsSymlink(absolutePath) || !visited.Add(absolutePath))
        {
            return;
        }

        string rel = ToRelative(absolutePath);

        // Sensitive files are never indexed - neither content nor path.
        if (_sensitive.IsIgnored(rel, isDirectory: false))
        {
            return;
        }

        if (IsIgnored(rel, isDirectory: false, scopes))
        {
            return;
        }

        var info = new FileInfo(absolutePath);
        if (info.Length > (long)_config.Indexing.MaxFileSizeKb * 1024)
        {
            return;
        }

        if (FileClassifier.IsBinaryExtension(rel) || IsBinaryContent(absolutePath))
        {
            return;
        }

        FileKind kind = FileClassifier.ClassifyKind(rel);
        if (kind == FileKind.Test && !_config.Indexing.IncludeTests)
        {
            return;
        }

        if (kind == FileKind.Doc && !_config.Indexing.IncludeDocs)
        {
            return;
        }

        results.Add(new ScannedFile
        {
            AbsolutePath = absolutePath,
            RelativePath = rel,
            Kind = kind,
            Language = FileClassifier.DetectLanguage(rel),
            SizeBytes = info.Length,
        });
    }

    /// <summary>
    /// Applies the ignore scopes outermost-first so the innermost rule that
    /// matches decides - gitignore's "a subdirectory's file overrides the
    /// higher one" precedence, including <c>!</c> re-inclusion.
    /// </summary>
    private static bool IsIgnored(string relativePath, bool isDirectory, List<IgnoreScope> scopes)
    {
        bool ignored = false;
        foreach (IgnoreScope scope in scopes)
        {
            if (scope.TryScope(relativePath, out string scoped)
                && scope.Matcher.Match(scoped, isDirectory) is bool decision)
            {
                ignored = decision;
            }
        }

        return ignored;
    }

    private void AddIgnoreFiles(string directory, List<IgnoreScope> scopes)
    {
        string baseRelative = ToRelative(directory);
        if (baseRelative is ".")
        {
            baseRelative = string.Empty;
        }

        // .gitignore first so a .repoctxignore at the same level can override it.
        if (_config.RespectGitignore)
        {
            AddIfPresent(Path.Combine(directory, ".gitignore"));
        }

        AddIfPresent(Path.Combine(directory, ".repoctxignore"));

        void AddIfPresent(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                scopes.Add(new IgnoreScope(baseRelative, GitignoreMatcher.Parse(File.ReadAllLines(path))));
            }
            catch (IOException)
            {
                // An unreadable ignore file must not abort the scan.
            }
        }
    }

    private bool HasIgnoreFile(string directory) =>
        (_config.RespectGitignore && File.Exists(Path.Combine(directory, ".gitignore")))
        || File.Exists(Path.Combine(directory, ".repoctxignore"));

    private string ToRelative(string absolutePath) =>
        Path.GetRelativePath(_repoRoot, absolutePath).Replace('\\', '/');

    private static bool IsSymlink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsBinaryContent(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[SniffBytes];
            int read = stream.Read(buffer);
            return FileClassifier.LooksBinary(buffer[..read]);
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// One ignore file (or the configured excludes) together with the
    /// repo-relative directory its patterns are written against.
    /// </summary>
    private sealed record IgnoreScope(string BaseRelative, GitignoreMatcher Matcher)
    {
        /// <summary>
        /// Rewrites a repo-relative path into this scope's coordinate system.
        /// Returns false when the path lies outside the scope's subtree.
        /// </summary>
        public bool TryScope(string relativePath, out string scoped)
        {
            if (BaseRelative.Length == 0)
            {
                scoped = relativePath;
                return true;
            }

            if (relativePath.Length > BaseRelative.Length
                && relativePath.StartsWith(BaseRelative, StringComparison.Ordinal)
                && relativePath[BaseRelative.Length] == '/')
            {
                scoped = relativePath[(BaseRelative.Length + 1)..];
                return true;
            }

            scoped = relativePath;
            return false;
        }
    }
}
