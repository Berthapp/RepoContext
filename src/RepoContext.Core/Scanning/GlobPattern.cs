using System.Text;
using System.Text.RegularExpressions;

namespace RepoContext.Core.Scanning;

/// <summary>
/// Translates a single gitignore/glob pattern into an anchored regular
/// expression. Supports <c>*</c> (not crossing <c>/</c>), <c>?</c>, and
/// <c>**</c> (crossing directories).
/// </summary>
internal static class GlobPattern
{
    public static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    bool doubleStar = i + 1 < glob.Length && glob[i + 1] == '*';
                    if (doubleStar)
                    {
                        i++;
                        // "**/" consumes an optional run of leading segments.
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }

                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }
}
