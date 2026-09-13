namespace RepoContext.Core.Guard;

/// <summary>Safe rendering of the commands a denial suggests.</summary>
/// <remarks>
/// The paths in a suggestion come from a tool call, which is untrusted input.
/// The suggestion is handed to a model that may run it, so a path containing a
/// quote, a space or a shell metacharacter must come back as one literal
/// argument and never as a second command.
/// </remarks>
public static class GuardSuggestion
{
    /// <summary>
    /// Quotes a path for a POSIX shell, leaving plainly safe paths bare so the
    /// common suggestion stays readable.
    /// </summary>
    /// <remarks>
    /// Single quotes are used because they suppress every expansion; an embedded
    /// single quote is closed, escaped and reopened
    /// (<c>it's</c> becomes <c>'it'\''s'</c>), which is the only form that
    /// survives every POSIX shell. PowerShell users get a correctly quoted POSIX
    /// string, which is still one literal argument to the shells that run these
    /// suggestions, and never an injection.
    /// </remarks>
    public static string Quote(string path)
    {
        if (path.Length > 0 && IsPlain(path))
        {
            return path;
        }

        return "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool IsPlain(string path)
    {
        foreach (char c in path)
        {
            bool safe = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or '+' or '@';
            if (!safe)
            {
                return false;
            }
        }

        return true;
    }
}
