using RepoContext.Core.Configuration;

namespace RepoContext.Core.Scanning;

/// <summary>
/// Walks a repository and selects files for indexing, applying include roots,
/// exclude / <c>.gitignore</c> / <c>.repoctxignore</c> rules, sensitive-file
/// exclusion, binary detection, size limits and kind classification (spec F2).
/// Output is deterministic (ordered by path, Ordinal).
/// </summary>
public sealed class FileScanner
{
    private const int SniffBytes = 8000;

    private readonly string _repoRoot;
    private readonly RepoctxConfig _config;
    private readonly GitignoreMatcher _exclude;
    private readonly GitignoreMatcher _sensitive;
    private readonly GitignoreMatcher _repoctxIgnore;
    private readonly GitignoreMatcher _gitIgnore;

    public FileScanner(string repoRoot, RepoctxConfig config)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _exclude = GitignoreMatcher.FromGlobs(config.Exclude);
        _sensitive = GitignoreMatcher.FromGlobs(config.SensitiveFiles);
        _repoctxIgnore = ReadIgnoreFile(Path.Combine(_repoRoot, ".repoctxignore"));
        _gitIgnore = config.RespectGitignore
            ? ReadIgnoreFile(Path.Combine(_repoRoot, ".gitignore"))
            : GitignoreMatcher.Empty;
    }

    /// <summary>Returns whether a repo-relative path is treated as sensitive.</summary>
    public bool IsSensitive(string relativePath) => _sensitive.IsIgnored(relativePath, isDirectory: false);

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
                Walk(abs, results, visited);
            }
            else if (File.Exists(abs))
            {
                TryAddFile(abs, results, visited);
            }
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return results;
    }

    private void Walk(string directory, List<ScannedFile> results, HashSet<string> visited)
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

                if (IsIgnored(rel, isDirectory: true))
                {
                    continue;
                }

                Walk(entry, results, visited);
            }
            else if (File.Exists(entry))
            {
                TryAddFile(entry, results, visited);
            }
        }
    }

    private void TryAddFile(string absolutePath, List<ScannedFile> results, HashSet<string> visited)
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

        if (IsIgnored(rel, isDirectory: false))
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

    private bool IsIgnored(string relativePath, bool isDirectory) =>
        _exclude.IsIgnored(relativePath, isDirectory)
        || _repoctxIgnore.IsIgnored(relativePath, isDirectory)
        || _gitIgnore.IsIgnored(relativePath, isDirectory);

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

    private static GitignoreMatcher ReadIgnoreFile(string path) =>
        File.Exists(path) ? GitignoreMatcher.Parse(File.ReadAllLines(path)) : GitignoreMatcher.Empty;
}
