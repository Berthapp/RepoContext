using System.Text;

namespace RepoContext.Core.Context;

/// <summary>
/// The opt-in lossy slice transform behind <c>context --strip-comments</c>
/// (ADR 0012): drops full-line <c>//</c> comments and full-line
/// <c>/* ... */</c> blocks, and collapses blank runs — the doc content is
/// already summarized in outlines, so verbose comment banners are usually
/// paid twice. Line-based and deliberately conservative: lines mixing code
/// and comments are kept untouched, so no code is ever lost. Covers all four
/// indexed languages (TypeScript, TSX, JavaScript, C#), which share C-family
/// comment syntax. Deterministic.
/// </summary>
public static class CommentStripper
{
    /// <summary>
    /// Strips <paramref name="text"/>; <c>changed</c> tells the caller whether
    /// anything was removed (and line ranges therefore became approximate).
    /// </summary>
    public static (string Text, bool Changed) Strip(string text)
    {
        string[] lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        bool changed = false;
        bool inBlock = false;
        bool lastBlank = false;

        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();

            if (inBlock)
            {
                changed = true;
                int close = trimmed.IndexOf("*/", StringComparison.Ordinal);
                if (close >= 0)
                {
                    inBlock = false;
                    string tail = trimmed[(close + 2)..].Trim();
                    if (tail.Length > 0)
                    {
                        kept.Add(tail); // rare code-after-close; keep the code
                        lastBlank = false;
                    }
                }

                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                changed = true;
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                int close = trimmed.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0)
                {
                    inBlock = true;
                    changed = true;
                    continue;
                }

                string tail = trimmed[(close + 2)..].Trim();
                if (tail.Length == 0)
                {
                    changed = true;
                    continue;
                }
            }

            bool blank = trimmed.Length == 0;
            if (blank && lastBlank)
            {
                changed = true;
                continue; // collapse blank runs
            }

            kept.Add(line);
            lastBlank = blank;
        }

        // Trim leading/trailing blanks the stripping may have exposed.
        int start = 0;
        int end = kept.Count;
        while (start < end && kept[start].Trim().Length == 0)
        {
            start++;
            changed = true;
        }

        while (end > start && kept[end - 1].Trim().Length == 0)
        {
            end--;
            changed = true;
        }

        if (!changed)
        {
            return (text, false);
        }

        var sb = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            if (i > start)
            {
                sb.Append('\n');
            }

            sb.Append(kept[i]);
        }

        return (sb.ToString(), true);
    }
}
