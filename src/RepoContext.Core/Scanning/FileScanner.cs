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

    /// <summary>Oversized paths remembered for the report; the count is exact, the list is not.</summary>
    private const int MaxReportedOversized = 5;

    private readonly string _repoRoot;
    private readonly RepoctxConfig _config;
    private readonly GitignoreMatcher _sensitive;
    private readonly IgnoreScope _exclude;
    private readonly List<string> _oversized = [];
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadableDirectories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadableIgnoreFiles = new(StringComparer.Ordinal);

    public FileScanner(string repoRoot, RepoctxConfig config)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _sensitive = GitignoreMatcher.FromGlobs(config.SensitiveFiles);
        _exclude = new IgnoreScope(string.Empty, GitignoreMatcher.FromGlobs(config.Exclude));
    }

    /// <summary>
    /// How many files the last scan skipped for exceeding
    /// <c>indexing.maxFileSizeKb</c>. A skipped file is invisible to every
    /// query, and an unreported one is invisible to the user too - the silent
    /// coverage gap ADR 0017 set out to remove. Large exported artifacts are
    /// exactly the files this limit tends to catch.
    /// </summary>
    public int OversizedCount { get; private set; }

    /// <summary>The first few oversized paths, for a report the user can act on.</summary>
    public IReadOnlyList<string> OversizedSample => _oversized;

    /// <summary>
    /// How many files the last scan skipped because they are binary. Reported
    /// so "everything is indexed" stays an auditable claim rather than a
    /// promise: binary files are the one category RepoContext cannot describe,
    /// and the user is entitled to know how big it is.
    /// </summary>
    public int BinaryCount { get; private set; }

    /// <summary>
    /// How many files the last scan could not open at all - a permission the
    /// process lacks, or a lock held elsewhere. Counted rather than mistaken for
    /// binary, and above all not thrown: one unreadable file must not abort an
    /// index run over a repository of thousands.
    /// </summary>
    public int UnreadableCount => _unreadable.Count;

    /// <summary>
    /// The repo-relative paths the last scan could not open. Callers need the
    /// paths, not just the count: a file that is already indexed has to be held
    /// on to, or a moment's lock costs it its index row.
    /// </summary>
    /// <remarks>
    /// This is the reporting surface: it names files and directories alike -
    /// on a full rebuild there is no index to notice a missing subtree by, so
    /// without the directories a whole tree would disappear with exit code 0 -
    /// and it never contains the internal root sentinel or a path the
    /// configuration excluded from view.
    /// </remarks>
    public IReadOnlyCollection<string> UnreadablePaths =>
    [
        .. _unreadable
            .Concat(_unreadableDirectories.Select(d => d.Length == 0 ? "." : d))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>
    /// Ignore files whose rules could not be read. A different problem from an
    /// unreadable source file: nothing is missing from the index because of it,
    /// something is <i>extra</i> - the directories those rules would have
    /// excluded were walked and indexed. Reported on its own for that reason.
    /// </summary>
    public IReadOnlyCollection<string> UnreadableIgnoreFiles => _unreadableIgnoreFiles;

    /// <summary>
    /// Whether the last scan failed to look at <paramref name="relativePath"/> -
    /// the file itself was unreadable, or a directory above it could not be
    /// entered. A caller holding an index must retain such a path: the scan
    /// says nothing about it, and treating silence as deletion wipes a subtree
    /// on a transient mount failure.
    /// </summary>
    public bool WasSkipped(string relativePath)
    {
        if (_unreadable.Contains(relativePath))
        {
            return true;
        }

        // Prefixes are deduplicated and, in any healthy repository, a handful:
        // one per directory or entry the scan could not classify. The linear
        // scan over them is the whole cost of the check.
        foreach (string directory in _unreadableDirectories)
        {
            if (IsUnder(relativePath, directory))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a path lies inside a directory, where an empty directory is the root.</summary>
    private static bool IsUnder(string relativePath, string directory) =>
        directory.Length == 0
        || (relativePath.Length > directory.Length
            && relativePath.StartsWith(directory, StringComparison.Ordinal)
            && relativePath[directory.Length] == '/');

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
        OversizedCount = 0;
        BinaryCount = 0;
        _oversized.Clear();
        _unreadable.Clear();
        _unreadableDirectories.Clear();
        _unreadableIgnoreFiles.Clear();
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
            RecordUnreadableDirectory(directory);
            return;
        }
        catch (IOException)
        {
            // Removed mid-walk, or an unreadable mount point. One directory the
            // scan cannot enumerate is not a reason to abandon the repository -
            // but it is a reason to remember it, because everything already
            // indexed below it would otherwise look deleted.
            RecordUnreadableDirectory(directory);
            return;
        }

        Array.Sort(entries, StringComparer.Ordinal);

        foreach (string entry in entries)
        {
            if (!TryGetAttributes(entry, out FileAttributes attributes))
            {
                // Present but inaccessible: it cannot even be classified, so it
                // is recorded and skipped rather than crashing the scan. Whether
                // it is a file or a directory is exactly what could not be
                // established, so it is recorded as both - anything already
                // indexed beneath it must be retained too.
                RecordUnreadableEntry(ToRelative(entry), scopes);
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
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

        long length;
        try
        {
            length = new FileInfo(absolutePath).Length;
        }
        catch (IOException)
        {
            _unreadable.Add(rel);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            _unreadable.Add(rel);
            return;
        }

        if (length > (long)_config.Indexing.MaxFileSizeKb * 1024)
        {
            // Reported rather than silently dropped: see OversizedCount.
            if (!FileClassifier.IsBinaryExtension(rel))
            {
                OversizedCount++;
                if (_oversized.Count < MaxReportedOversized)
                {
                    _oversized.Add(rel);
                }
            }

            return;
        }

        if (FileClassifier.IsBinaryExtension(rel))
        {
            BinaryCount++;
            return;
        }

        switch (Sniff(absolutePath))
        {
            case Content.Binary:
                BinaryCount++;
                return;
            case Content.Unreadable:
                _unreadable.Add(rel);
                return;
            default:
                break;
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
            SizeBytes = length,
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
                // An unreadable ignore file must not abort the scan - but its
                // rules are then not applied, which changes what gets indexed,
                // so it is reported rather than silently skipped.
                _unreadableIgnoreFiles.Add(ToRelative(path));
            }
            catch (UnauthorizedAccessException)
            {
                _unreadableIgnoreFiles.Add(ToRelative(path));
            }
        }
    }

    private bool HasIgnoreFile(string directory) =>
        (_config.RespectGitignore && File.Exists(Path.Combine(directory, ".gitignore")))
        || File.Exists(Path.Combine(directory, ".repoctxignore"));

    private string ToRelative(string absolutePath) =>
        Path.GetRelativePath(_repoRoot, absolutePath).Replace('\\', '/');

    /// <summary>
    /// Records a directory the scan could not enter, so callers retain what they
    /// already hold beneath it. The repository root relativizes to <c>.</c>,
    /// which is normalized to the empty prefix meaning "everything".
    /// </summary>
    private void RecordUnreadableDirectory(string absolutePath)
    {
        string relative = ToRelative(absolutePath);
        _unreadableDirectories.Add(relative == "." ? string.Empty : relative);
    }

    /// <summary>
    /// Records an entry that could not be classified at all, unless it is
    /// sensitive or excluded - those must never surface as a path, which is the
    /// whole point of the setting, and a caller reports what it is told here.
    /// Since file or directory is precisely what is unknown, both readings are
    /// tested and both are recorded.
    /// </summary>
    private void RecordUnreadableEntry(string relative, List<IgnoreScope> scopes)
    {
        // Excluded under *either* reading means it is never recorded. File or
        // directory is precisely what could not be established, and a
        // trailing-slash pattern (`secrets/`, `node_modules/`) only ever
        // matches the directory reading - so deciding the two independently
        // would publish the name of a path the configuration removed from view.
        // The cost of being conservative is a rare pruned row that the next
        // successful run restores; the cost of the alternative is a leaked
        // sensitive path, which nothing restores.
        if (IsExcluded(relative, isDirectory: false, scopes)
            || IsExcluded(relative, isDirectory: true, scopes))
        {
            return;
        }

        _unreadable.Add(relative);
        _unreadableDirectories.Add(relative);
    }

    /// <summary>
    /// Whether a path is sensitive or excluded under one reading. Such a path is
    /// never surfaced - "neither content nor path" is the whole point of the
    /// sensitive setting, and callers report what they are told here.
    /// </summary>
    private bool IsExcluded(string relative, bool isDirectory, List<IgnoreScope> scopes) =>
        _sensitive.IsIgnored(relative, isDirectory)
        || IsIgnored(relative, isDirectory, scopes);

    /// <summary>
    /// Reads an entry's attributes. Returns false when it is present but
    /// inaccessible - a permission the process lacks. A vanished entry reports
    /// success with default attributes, since "not there" is not a failure the
    /// caller has to surface.
    /// </summary>
    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        attributes = default;
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsSymlink(string path) =>
        TryGetAttributes(path, out FileAttributes attributes)
        && (attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>What sniffing a file's first bytes established about it.</summary>
    private enum Content
    {
        Text,
        Binary,
        Unreadable,
    }

    /// <summary>
    /// Classifies a file by its first bytes. A file that cannot be opened is
    /// reported as such rather than as binary: the two have different causes and
    /// different fixes, and calling a locked file binary hides it inside a
    /// category the user is told is unfixable.
    /// </summary>
    private static Content Sniff(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[SniffBytes];
            int read = stream.Read(buffer);
            return FileClassifier.LooksBinary(buffer[..read]) ? Content.Binary : Content.Text;
        }
        catch (IOException)
        {
            return Content.Unreadable;
        }
        catch (UnauthorizedAccessException)
        {
            return Content.Unreadable;
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
