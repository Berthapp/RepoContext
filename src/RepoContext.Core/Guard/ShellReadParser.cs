using System.Globalization;

namespace RepoContext.Core.Guard;

/// <summary>What a shell command turned out to be.</summary>
public enum ShellReadKind
{
    /// <summary>
    /// The guard does not understand this command well enough to judge it. The
    /// client handles it normally. Deliberately the default: a cost policy that
    /// guesses at shell semantics would block work it never understood.
    /// </summary>
    Unsupported,

    /// <summary>A command in the supported subset that reads no file content.</summary>
    NotARead,

    /// <summary>A command in the supported subset that reads file content.</summary>
    Read,
}

/// <summary>The parse of one shell command.</summary>
/// <param name="Kind">What the command turned out to be.</param>
/// <param name="Reads">The file reads it performs, empty unless <see cref="Kind"/> is <see cref="ShellReadKind.Read"/>.</param>
/// <param name="Note">Why an unsupported command was not judged, for the counters and for humans.</param>
public sealed record ShellReadParse(
    ShellReadKind Kind, IReadOnlyList<GuardReadRequest> Reads, string Note);

/// <summary>
/// A deliberately small, documented parser for the shell commands that are
/// plainly whole-file reads (milestone 2 of the same-quality, lower-cost plan).
/// </summary>
/// <remarks>
/// <para>
/// The guard never executes a command to discover what it would read. Everything
/// here is static inspection of the command text, and anything outside the
/// documented subset is reported as <see cref="ShellReadKind.Unsupported"/> so
/// the client's normal handling applies. That asymmetry is the point: a missed
/// read costs tokens, a wrongly parsed one costs the user their work.
/// </para>
/// <para>
/// Explicitly out of subset — and therefore never judged: pipelines,
/// redirections, command substitution, variable expansion, globbing, brace
/// expansion, command lists, subshells, scripts, and any command that also
/// writes. A broad regular expression over the command text would "cover" all of
/// those and be wrong about most of them.
/// </para>
/// <para>
/// Supported readers: <c>cat</c>, <c>head</c>, <c>tail</c>, <c>nl</c>,
/// <c>bat</c>, <c>less</c>, <c>more</c>, <c>type</c> and the single most common
/// bounded form of <c>sed</c> (<c>sed -n '12,40p' FILE</c>). Quoting, paths
/// containing spaces, Windows-style paths and several input files are covered by
/// the word splitter and by fixtures.
/// </para>
/// </remarks>
public static class ShellReadParser
{
    /// <summary>Characters that make a command line more than one plain command.</summary>
    private static readonly char[] ShellMetacharacters =
        ['|', '&', ';', '<', '>', '`', '(', ')', '\n', '\r', '*', '?', '[', ']', '{', '}', '!', '#'];

    private static readonly string[] Readers =
        ["cat", "head", "tail", "nl", "bat", "batcat", "less", "more", "type"];

    /// <summary>
    /// Commands that are cheap, well understood and read no file content, so the
    /// guard can record them as seen without pretending to judge a read.
    /// </summary>
    private static readonly string[] NonReaders =
        ["ls", "pwd", "cd", "echo", "git", "grep", "rg", "find", "wc", "which", "repoctx"];

    /// <summary>Parses one command line.</summary>
    public static ShellReadParse Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Unsupported("empty command");
        }

        if (command.IndexOfAny(ShellMetacharacters) >= 0)
        {
            return Unsupported("pipeline, redirection, expansion or command list");
        }

        if (command.Contains('$', StringComparison.Ordinal))
        {
            return Unsupported("variable or command substitution");
        }

        if (!TrySplit(command, out List<string> words) || words.Count == 0)
        {
            return Unsupported("unbalanced quoting");
        }

        string program = Program(words[0]);
        string[] arguments = [.. words.Skip(1)];

        if (Array.Exists(NonReaders, c => c == program))
        {
            return new ShellReadParse(ShellReadKind.NotARead, [], program);
        }

        if (program == "sed")
        {
            return ParseSed(arguments);
        }

        if (!Array.Exists(Readers, c => c == program))
        {
            return Unsupported("command outside the supported read subset");
        }

        return ParseReader(program, arguments);
    }

    /// <summary>
    /// Splits a command into words, honouring single quotes, double quotes and
    /// backslash escapes.
    /// </summary>
    /// <remarks>
    /// Outside quotes a backslash escapes only the characters a shell would
    /// treat specially here (space, quotes, backslash). Every other backslash is
    /// literal, which is what keeps <c>C:\src\app.ts</c> intact instead of
    /// silently becoming <c>C:srcapp.ts</c> and resolving to the wrong file.
    /// </remarks>
    public static bool TrySplit(string command, out List<string> words)
    {
        words = [];
        var current = new System.Text.StringBuilder();
        bool inWord = false;
        char quote = '\0';

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = '\0';
                }
                else if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\')
                {
                    current.Append(command[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    inWord = true;
                    break;
                case '\\' when i + 1 < command.Length && command[i + 1] is ' ' or '\'' or '"' or '\\':
                    current.Append(command[++i]);
                    inWord = true;
                    break;
                case ' ' or '\t':
                    if (inWord)
                    {
                        words.Add(current.ToString());
                        current.Clear();
                        inWord = false;
                    }

                    break;
                default:
                    current.Append(c);
                    inWord = true;
                    break;
            }
        }

        if (quote != '\0')
        {
            words = [];
            return false;
        }

        if (inWord)
        {
            words.Add(current.ToString());
        }

        return true;
    }

    /// <summary>The program name without its directory or Windows extension.</summary>
    private static string Program(string word)
    {
        string name = word.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.ToLowerInvariant();
    }

    private static ShellReadParse ParseReader(string program, string[] arguments)
    {
        int? lineLimit = null;
        var paths = new List<string>();

        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (argument == "--")
            {
                paths.AddRange(arguments[(i + 1)..]);
                break;
            }

            if (argument.StartsWith('-') && argument.Length > 1)
            {
                if (program is "head" or "tail")
                {
                    if (argument == "-n" && i + 1 < arguments.Length
                        && TryCount(arguments[++i], out int separate))
                    {
                        lineLimit = separate;
                        continue;
                    }

                    if (argument.StartsWith("-n", StringComparison.Ordinal)
                        && TryCount(argument[2..], out int joined))
                    {
                        lineLimit = joined;
                        continue;
                    }
                }

                // An option the subset does not model can change what is read
                // (head -c, bat --line-range, less +G). Refuse to guess.
                return Unsupported($"unmodelled option '{argument}'");
            }

            paths.Add(argument);
        }

        if (paths.Count == 0)
        {
            // No path means standard input; there is no file to price.
            return new ShellReadParse(ShellReadKind.NotARead, [], program + " without a path");
        }

        // A bare head/tail defaults to ten lines, which is never worth blocking.
        if (program is "head" or "tail" && lineLimit is null)
        {
            lineLimit = 10;
        }

        return new ShellReadParse(
            ShellReadKind.Read,
            [.. paths.Select(path => new GuardReadRequest(
                path, program == "head" ? 1 : null, lineLimit, "shell:" + program))],
            program);
    }

    /// <summary>Parses <c>sed -n '12,40p' FILE</c> and nothing more adventurous.</summary>
    private static ShellReadParse ParseSed(string[] arguments)
    {
        string[] rest = arguments;
        if (rest.Length < 2 || rest[0] != "-n")
        {
            return Unsupported("only 'sed -n <start>,<end>p <file>' is modelled");
        }

        string script = rest[1];
        if (!script.EndsWith('p') || script.Length < 4)
        {
            return Unsupported("only 'sed -n <start>,<end>p <file>' is modelled");
        }

        string[] bounds = script[..^1].Split(',');
        if (bounds.Length != 2
            || !TryCount(bounds[0], out int start)
            || !TryCount(bounds[1], out int end)
            || end < start)
        {
            return Unsupported("only 'sed -n <start>,<end>p <file>' is modelled");
        }

        string[] paths = rest[2..];
        if (paths.Length == 0 || Array.Exists(paths, p => p.StartsWith('-')))
        {
            return Unsupported("only 'sed -n <start>,<end>p <file>' is modelled");
        }

        return new ShellReadParse(
            ShellReadKind.Read,
            [.. paths.Select(path => new GuardReadRequest(
                path, start, end - start + 1, "shell:sed"))],
            "sed");
    }

    private static bool TryCount(string value, out int count) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count)
        && count > 0;

    private static ShellReadParse Unsupported(string note) =>
        new(ShellReadKind.Unsupported, [], note);
}
