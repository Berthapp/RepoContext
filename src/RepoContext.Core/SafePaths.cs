using System.Text;

namespace RepoContext.Core;

/// <summary>
/// Raised when RepoContext refuses to write a file because a symbolic link
/// would carry the write somewhere it must not go.
/// </summary>
/// <remarks>
/// An <see cref="IOException"/> on purpose: every best-effort writer (usage
/// ledger, sessions, guard state) already treats an I/O failure as "skip this
/// write", which is exactly the right degradation for a refused one.
/// </remarks>
public sealed class UnsafePathException(string message) : IOException(message);

/// <summary>
/// Keeps every file RepoContext writes where it belongs.
/// </summary>
/// <remarks>
/// <para>
/// A cloned repository is untrusted input, and git checks symbolic links out
/// verbatim. Without these checks a committed <c>CLAUDE.md -&gt; ~/.bashrc</c>,
/// <c>.repoctx -&gt; /elsewhere</c> or <c>.repoctx/stats.html -&gt; ~/.profile</c>
/// turns an ordinary <c>init</c>, <c>index</c>, <c>integrate</c> or <c>stats</c>
/// into a write outside the repository the user pointed the tool at.
/// </para>
/// <para>
/// Two rules, for two kinds of file. Files a repository owns and a user may
/// legitimately link (<c>CLAUDE.md -&gt; AGENTS.md</c> is common) only have to
/// <i>resolve</i> inside the repository: <see cref="EnsureContained"/>. The
/// index directory is RepoContext's own, nobody has a reason to link anything
/// inside it, and it holds a SQLite database whose side files a link could
/// redirect; there no link is accepted at all: <see cref="EnsureNoLinks"/>.
/// </para>
/// <para>
/// The checks protect against a hostile <i>checkout</i>. A process that can
/// already swap links under a running command owns the account it runs in, and
/// no path check can outrun that.
/// </para>
/// </remarks>
public static class SafePaths
{
    /// <summary>More link hops than this is a cycle, or close enough to one.</summary>
    private const int MaxLinkHops = 40;

    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Whether <paramref name="path"/>, with every symbolic link on the way
    /// resolved, lies inside <paramref name="root"/> (also resolved). A path that
    /// cannot be resolved is treated as outside.
    /// </summary>
    public static bool IsContained(string root, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(Resolve(root), Resolve(path)).Replace('\\', '/');
            return relative != ".."
                && !relative.StartsWith("../", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Throws <see cref="UnsafePathException"/> unless <paramref name="path"/>
    /// resolves inside <paramref name="root"/>.
    /// </summary>
    public static void EnsureContained(string root, string path)
    {
        if (!IsContained(root, path))
        {
            throw new UnsafePathException(
                $"Refusing to write '{path}': a symbolic link resolves it outside the repository "
                + $"'{root}'. Replace the link with a regular file and retry.");
        }
    }

    /// <summary>
    /// Throws <see cref="UnsafePathException"/> when <paramref name="baseDirectory"/>
    /// or any entry between it and <paramref name="path"/> (inclusive) is a
    /// symbolic link or junction. Entries that do not exist yet are fine: they
    /// will be created as real ones.
    /// </summary>
    public static void EnsureNoLinks(string baseDirectory, string path)
    {
        string basePath = Path.GetFullPath(baseDirectory);
        string relative = Path.GetRelativePath(basePath, Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new UnsafePathException($"'{path}' is not inside '{baseDirectory}'.");
        }

        string current = basePath;
        RejectLink(current);
        if (relative == ".")
        {
            return;
        }

        foreach (string part in relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            RejectLink(current);
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="content"/> (UTF-8,
    /// no BOM) through a freshly created temporary file and an atomic rename.
    /// </summary>
    /// <remarks>
    /// The staging file is <c>&lt;path&gt;.tmp</c>. Whatever already sits there -
    /// a leftover from a crashed writer, or a link a hostile checkout planted to
    /// receive the content - is removed first (a link is unlinked, never
    /// followed), and the staging file is then created exclusively, so the
    /// content only ever lands in a regular file this call created. Something
    /// that cannot be removed, such as a directory, fails the write; callers
    /// already treat that as an ordinary I/O failure. The rename replaces the
    /// target's directory entry and never writes through it. Callers serialize
    /// writers of one path, as every store does with its lock.
    /// </remarks>
    public static void WriteAllTextAtomic(string path, string content)
    {
        string full = Path.GetFullPath(path);
        string temporary = full + ".tmp";
        File.Delete(temporary);
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // GetBytes never emits a byte-order mark, matching File.WriteAllText.
                stream.Write(Encoding.UTF8.GetBytes(content));
            }

            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Resolves every symbolic link in <paramref name="path"/>, including links
    /// that dangle, walking down from the filesystem root.
    /// </summary>
    /// <remarks>
    /// <c>..</c> is applied to the resolved (physical) location, never to the
    /// spelling: a link target such as <c>sub/../x</c>, where <c>sub</c> is itself
    /// a link, names a location that lexical normalization would get wrong.
    /// </remarks>
    private static string Resolve(string path)
    {
        string absolute = Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);
        string current = Path.GetPathRoot(absolute) ?? string.Empty;
        var pending = new Stack<string>(Parts(absolute[current.Length..]).Reverse());
        int hops = 0;

        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            string next = Path.Combine(current, part);
            if (LinkTarget(next) is not { } target)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new UnsafePathException($"Too many symbolic links resolving '{path}'.");
            }

            // A relative target is relative to the directory holding the link,
            // which is `current`; an absolute one restarts at its own root.
            if (Path.GetPathRoot(target) is { Length: > 0 } targetRoot)
            {
                current = targetRoot;
                target = target[targetRoot.Length..];
            }

            foreach (string segment in Parts(target).Reverse())
            {
                pending.Push(segment);
            }
        }

        return current;
    }

    private static IEnumerable<string> Parts(string relative) =>
        relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static void RejectLink(string path)
    {
        if (LinkTarget(path) is not null)
        {
            throw new UnsafePathException(
                $"Refusing to use '{path}': it is a symbolic link. RepoContext reads and writes "
                + "its index directory only through real directories and files, so a link "
                + "committed to a repository cannot redirect its writes. Replace the link and retry.");
        }
    }

    /// <summary>
    /// The immediate target of a link, or null for anything that is not one -
    /// including a path that does not exist. Reads the entry itself, so a
    /// dangling link is still recognized as a link.
    /// </summary>
    private static string? LinkTarget(string path) =>
        new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
