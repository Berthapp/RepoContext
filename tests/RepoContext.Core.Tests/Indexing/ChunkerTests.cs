using RepoContext.Core.Indexing;
using RepoContext.Core.Scanning;

namespace RepoContext.Core.Tests.Indexing;

public class ChunkerTests
{
    [Fact]
    public void Markdown_SplitsByHeadings_WithHeadingText()
    {
        string md = "intro line\n# Title\nbody a\n## Authentication\nauth body\n";
        IReadOnlyList<Chunk> chunks = Chunker.Chunk(SourceLanguage.Markdown, md);

        Assert.Equal(ChunkKind.Preamble, chunks[0].Kind);
        Assert.Contains(chunks, c => c.Kind == ChunkKind.MarkdownHeading && c.Heading == "Authentication");
        Assert.Contains(chunks, c => c.Heading == "Title");
    }

    [Fact]
    public void Generic_EmitsPreambleThenBlocks_WithLineNumbers()
    {
        string code = string.Join('\n', Enumerable.Range(1, 150).Select(i => $"line {i}"));
        IReadOnlyList<Chunk> chunks = Chunker.Chunk(SourceLanguage.TypeScript, code);

        Assert.Equal(ChunkKind.Preamble, chunks[0].Kind);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(20, chunks[0].EndLine);
        Assert.All(chunks.Skip(1), c => Assert.Equal(ChunkKind.Block, c.Kind));
        // Contiguous, non-overlapping coverage of all lines.
        Assert.Equal(21, chunks[1].StartLine);
        Assert.Equal(150, chunks[^1].EndLine);
    }

    [Fact]
    public void ShortFile_ProducesSinglePreamble()
    {
        IReadOnlyList<Chunk> chunks = Chunker.Chunk(SourceLanguage.TypeScript, "a\nb\nc");
        Assert.Single(chunks);
        Assert.Equal(3, chunks[0].EndLine);
    }

    [Fact]
    public void Empty_ProducesNoChunks()
    {
        Assert.Empty(Chunker.Chunk(SourceLanguage.TypeScript, string.Empty));
    }
}
