using System.Text;
using System.Text.RegularExpressions;
using RepoContext.Core.Scanning;

namespace RepoContext.Core.Parsing;

/// <summary>Extracts signatures and doc comments for symbols.</summary>
internal static partial class DocExtractor
{
    /// <summary>The first meaningful line of a declaration, whitespace-collapsed.</summary>
    public static string Signature(string declarationText)
    {
        int newline = declarationText.IndexOf('\n');
        string firstLine = newline >= 0 ? declarationText[..newline] : declarationText;

        int brace = firstLine.IndexOf('{');
        if (brace >= 0)
        {
            firstLine = firstLine[..brace];
        }

        return WhitespaceRegex().Replace(firstLine, " ").Trim();
    }

    /// <summary>
    /// Extracts a JSDoc (<c>/** */</c>) or C# XML (<c>///</c>) comment immediately
    /// above line <paramref name="declarationRow"/> (0-based). Returns null if none.
    /// </summary>
    public static string? DocAbove(SourceLanguage language, string[] lines, int declarationRow)
    {
        return language == SourceLanguage.CSharp
            ? XmlDoc(lines, declarationRow)
            : JsDoc(lines, declarationRow);
    }

    private static string? XmlDoc(string[] lines, int declarationRow)
    {
        var collected = new List<string>();
        for (int i = declarationRow - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("///", StringComparison.Ordinal))
            {
                collected.Add(line[3..].Trim());
                continue;
            }

            if (line.StartsWith('[') || line.Length == 0)
            {
                // Skip attributes / blank lines between doc and declaration.
                if (collected.Count > 0)
                {
                    break;
                }

                continue;
            }

            break;
        }

        if (collected.Count == 0)
        {
            return null;
        }

        collected.Reverse();
        string joined = string.Join(' ', collected);
        Match summary = SummaryRegex().Match(joined);
        string text = summary.Success ? summary.Groups[1].Value : joined;
        return Clean(StripTags(text));
    }

    private static string? JsDoc(string[] lines, int declarationRow)
    {
        int end = declarationRow - 1;
        while (end >= 0 && lines[end].Trim().Length == 0)
        {
            end--;
        }

        if (end < 0 || !lines[end].TrimEnd().EndsWith("*/", StringComparison.Ordinal))
        {
            return null;
        }

        int start = end;
        while (start >= 0 && !lines[start].TrimStart().StartsWith("/**", StringComparison.Ordinal))
        {
            start--;
        }

        if (start < 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (int i = start; i <= end; i++)
        {
            string line = lines[i].Trim();
            line = line.TrimStart('/');
            line = line.TrimStart('*');
            line = line.Replace("*/", string.Empty);
            line = line.TrimEnd('/').Trim();
            if (line.Length > 0 && !line.StartsWith('@'))
            {
                sb.Append(line).Append(' ');
            }
        }

        return Clean(sb.ToString());
    }

    private static string StripTags(string input) => TagRegex().Replace(input, " ");

    private static string? Clean(string input)
    {
        string cleaned = WhitespaceRegex().Replace(input, " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
}
