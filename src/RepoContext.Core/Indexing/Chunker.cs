using RepoContext.Core.Scanning;

namespace RepoContext.Core.Indexing;

/// <summary>
/// Splits file content into chunks (spec chapter 7). In M1: a file preamble,
/// markdown-heading sections, or fixed-size line blocks. Deterministic.
/// Symbol chunks follow in M2.
/// </summary>
public static class Chunker
{
    private const int PreambleLines = 20;
    private const int BlockLines = 60;

    public static IReadOnlyList<Chunk> Chunk(SourceLanguage language, string content)
    {
        string[] lines = SplitLines(content);
        if (lines.Length == 0)
        {
            return [];
        }

        return language == SourceLanguage.Markdown
            ? ChunkMarkdown(lines)
            : ChunkGeneric(lines);
    }

    private static IReadOnlyList<Chunk> ChunkGeneric(string[] lines)
    {
        var chunks = new List<Chunk>();

        int preambleEnd = Math.Min(PreambleLines, lines.Length);
        chunks.Add(new Chunk
        {
            Kind = ChunkKind.Preamble,
            StartLine = 1,
            EndLine = preambleEnd,
            Content = Join(lines, 0, preambleEnd),
        });

        for (int start = preambleEnd; start < lines.Length; start += BlockLines)
        {
            int end = Math.Min(start + BlockLines, lines.Length);
            chunks.Add(new Chunk
            {
                Kind = ChunkKind.Block,
                StartLine = start + 1,
                EndLine = end,
                Content = Join(lines, start, end),
            });
        }

        return chunks;
    }

    private static IReadOnlyList<Chunk> ChunkMarkdown(string[] lines)
    {
        var chunks = new List<Chunk>();
        int sectionStart = 0;
        string? heading = null;

        void Flush(int endExclusive)
        {
            if (endExclusive <= sectionStart)
            {
                return;
            }

            chunks.Add(new Chunk
            {
                Kind = heading is null ? ChunkKind.Preamble : ChunkKind.MarkdownHeading,
                StartLine = sectionStart + 1,
                EndLine = endExclusive,
                Content = Join(lines, sectionStart, endExclusive),
                Heading = heading,
            });
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (IsHeading(lines[i]))
            {
                Flush(i);
                sectionStart = i;
                heading = lines[i].TrimStart('#', ' ').Trim();
            }
        }

        Flush(lines.Length);
        return chunks;
    }

    private static bool IsHeading(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == '#')
        {
            i++;
        }

        return i is > 0 and <= 6 && i < line.Length && line[i] == ' ';
    }

    private static string Join(string[] lines, int start, int endExclusive) =>
        string.Join('\n', lines[start..endExclusive]);

    private static string[] SplitLines(string content)
    {
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Length == 0)
        {
            return [];
        }

        // Drop a single trailing newline so we don't emit a spurious empty line.
        if (normalized[^1] == '\n')
        {
            normalized = normalized[..^1];
        }

        return normalized.Split('\n');
    }
}
