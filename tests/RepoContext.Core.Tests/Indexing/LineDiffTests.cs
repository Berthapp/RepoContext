using System.Text;
using RepoContext.Core.Indexing;

namespace RepoContext.Core.Tests.Indexing;

/// <summary>Deterministic line diff behind <c>changed --patch</c> (ADR 0012).</summary>
public class LineDiffTests
{
    [Fact]
    public void IdenticalTexts_ProduceNoHunks()
    {
        Assert.Empty(LineDiff.Hunks("a\nb\nc", "a\nb\nc"));
    }

    [Fact]
    public void CrlfDifferences_AreNormalizedAway()
    {
        Assert.Empty(LineDiff.Hunks("a\r\nb\r\nc", "a\nb\nc"));
    }

    [Fact]
    public void SingleEdit_YieldsOneHunkWithContext()
    {
        string oldText = Lines("l1", "l2", "l3", "l4", "l5", "l6", "l7");
        string newText = Lines("l1", "l2", "l3", "CHANGED", "l5", "l6", "l7");

        IReadOnlyList<PatchHunk> hunks = LineDiff.Hunks(oldText, newText);

        PatchHunk hunk = Assert.Single(hunks);
        Assert.Equal(2, hunk.OldStart);   // two context lines before line 4
        Assert.Contains("-l4", hunk.Text);
        Assert.Contains("+CHANGED", hunk.Text);
        Assert.DoesNotContain("l1", hunk.Text); // outside the context window
        Assert.Equal(hunk.OldLines, hunk.NewLines);
    }

    [Fact]
    public void DistantEdits_YieldSeparateHunks()
    {
        string[] lines = Enumerable.Range(1, 40).Select(i => $"line{i}").ToArray();
        string[] edited = (string[])lines.Clone();
        edited[4] = "EDIT-A";
        edited[29] = "EDIT-B";

        IReadOnlyList<PatchHunk> hunks = LineDiff.Hunks(Lines(lines), Lines(edited));

        Assert.Equal(2, hunks.Count);
        Assert.Contains("+EDIT-A", hunks[0].Text);
        Assert.Contains("+EDIT-B", hunks[1].Text);
    }

    [Fact]
    public void NearbyEdits_MergeIntoOneHunk()
    {
        string[] lines = Enumerable.Range(1, 20).Select(i => $"line{i}").ToArray();
        string[] edited = (string[])lines.Clone();
        edited[7] = "EDIT-A";
        edited[10] = "EDIT-B"; // gap of 2 unchanged lines ≤ 2 * context

        IReadOnlyList<PatchHunk> hunks = LineDiff.Hunks(Lines(lines), Lines(edited));

        PatchHunk hunk = Assert.Single(hunks);
        Assert.Contains("+EDIT-A", hunk.Text);
        Assert.Contains("+EDIT-B", hunk.Text);
    }

    [Theory]
    [InlineData(true)]  // insertion
    [InlineData(false)] // deletion
    public void PureInsertionAndDeletion_RoundTrip(bool insert)
    {
        string[] baseLines = Enumerable.Range(1, 12).Select(i => $"b{i}").ToArray();
        var editedLines = baseLines.ToList();
        if (insert)
        {
            editedLines.InsertRange(6, ["new1", "new2"]);
        }
        else
        {
            editedLines.RemoveRange(3, 2);
        }

        AssertRoundTrip(Lines(baseLines), Lines([.. editedLines]));
    }

    [Fact]
    public void AppendAtEndOfFile_RoundTrips()
    {
        string oldText = Lines("a", "b", "c");
        AssertRoundTrip(oldText, oldText + "\nd\ne");
    }

    [Fact]
    public void ChangeAtFirstLine_RoundTrips()
    {
        AssertRoundTrip(Lines("first", "b", "c", "d"), Lines("FIRST", "b", "c", "d"));
    }

    [Fact]
    public void MixedEditClusters_RoundTrip()
    {
        string[] lines = Enumerable.Range(1, 60).Select(i => $"line{i}").ToArray();
        var edited = lines.ToList();
        edited[2] = "x2";
        edited.RemoveAt(10);
        edited.InsertRange(20, ["ins-a", "ins-b", "ins-c"]);
        edited[40] = "x40";
        edited[41] = "x41";
        edited.RemoveRange(50, 3);

        AssertRoundTrip(Lines(lines), Lines([.. edited]));
    }

    [Fact]
    public void PatchTokens_CountTheHunkBodies()
    {
        IReadOnlyList<PatchHunk> hunks = LineDiff.Hunks(Lines("a", "b", "c"), Lines("a", "B", "c"));

        Assert.True(LineDiff.PatchTokens(hunks) > 0);
        Assert.Equal(0, LineDiff.PatchTokens([]));
    }

    private static string Lines(params string[] lines) => string.Join('\n', lines);

    /// <summary>
    /// The strongest correctness check: applying the hunks to the old text
    /// must reproduce the new text exactly, and every context/removed line
    /// must match the old text at the position the hunk claims.
    /// </summary>
    private static void AssertRoundTrip(string oldText, string newText)
    {
        IReadOnlyList<PatchHunk> hunks = LineDiff.Hunks(oldText, newText);
        string[] oldLines = oldText.Split('\n');

        var result = new StringBuilder();
        int cursor = 0; // 0-based index into oldLines
        foreach (PatchHunk hunk in hunks)
        {
            for (; cursor < hunk.OldStart - 1; cursor++)
            {
                result.Append(oldLines[cursor]).Append('\n');
            }

            foreach (string line in hunk.Text.Split('\n'))
            {
                string content = line.Length > 0 ? line[1..] : string.Empty;
                switch (line.Length > 0 ? line[0] : ' ')
                {
                    case ' ':
                        Assert.Equal(oldLines[cursor], content);
                        result.Append(content).Append('\n');
                        cursor++;
                        break;
                    case '-':
                        Assert.Equal(oldLines[cursor], content);
                        cursor++;
                        break;
                    case '+':
                        result.Append(content).Append('\n');
                        break;
                }
            }

            Assert.Equal(hunk.OldStart - 1 + hunk.OldLines, cursor);
        }

        for (; cursor < oldLines.Length; cursor++)
        {
            result.Append(oldLines[cursor]).Append('\n');
        }

        Assert.Equal(newText, result.ToString().TrimEnd('\n'));
    }
}
