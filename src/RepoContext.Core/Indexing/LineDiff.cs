using System.Text;

namespace RepoContext.Core.Indexing;

/// <summary>One unified-style hunk: 1-based line anchors and a body whose lines
/// are prefixed with <c>' '</c> (context), <c>'-'</c> (removed) or <c>'+'</c> (added).</summary>
public sealed record PatchHunk(int OldStart, int OldLines, int NewStart, int NewLines, string Text);

/// <summary>
/// Deterministic line diff for <c>changed --patch</c> (ADR 0012): Myers'
/// O(ND) algorithm on lines, with common prefix/suffix trimmed first and a
/// hard cap on the edit distance. Beyond the cap (or on huge inputs) it falls
/// back to one hunk covering the whole differing region — still correct,
/// just less minimal. Same inputs ⇒ same hunks, byte for byte.
/// </summary>
public static class LineDiff
{
    /// <summary>Unchanged lines shown around each change.</summary>
    public const int ContextLines = 2;

    /// <summary>Myers cap: beyond this edit distance the single-hunk fallback is used.</summary>
    private const int MaxEditDistance = 2000;

    /// <summary>Input cap: above this many middle lines the fallback is used directly.</summary>
    private const int MaxDiffedLines = 20000;

    /// <summary>
    /// Computes the hunks that turn <paramref name="oldText"/> into
    /// <paramref name="newText"/>. Returns an empty list when the texts are
    /// line-identical (after newline normalization).
    /// </summary>
    public static IReadOnlyList<PatchHunk> Hunks(string oldText, string newText)
    {
        string[] a = SplitLines(oldText);
        string[] b = SplitLines(newText);

        // Trim the common prefix and suffix; edits are usually local.
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix
            && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        int midA = a.Length - prefix - suffix;
        int midB = b.Length - prefix - suffix;
        if (midA == 0 && midB == 0)
        {
            return [];
        }

        bool[] removed = new bool[a.Length];
        bool[] added = new bool[b.Length];
        if (!TryMarkMyers(a, b, prefix, midA, midB, removed, added))
        {
            // Fallback: everything between the common prefix and suffix changed.
            for (int i = prefix; i < prefix + midA; i++)
            {
                removed[i] = true;
            }

            for (int i = prefix; i < prefix + midB; i++)
            {
                added[i] = true;
            }
        }

        return BuildHunks(a, b, removed, added);
    }

    /// <summary>Sum of the hunk body tokens — what receiving the patch costs.</summary>
    public static int PatchTokens(IReadOnlyList<PatchHunk> hunks) =>
        hunks.Count == 0 ? 0 : Tokens.Count(string.Join('\n', hunks.Select(h => h.Text)));

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Myers' greedy O(ND) forward pass over the trimmed middle, with a full
    /// V-trace for backtracking. Marks removed/added lines (absolute indices).
    /// Returns false when the caps are exceeded and the fallback should apply.
    /// </summary>
    private static bool TryMarkMyers(
        string[] a, string[] b, int offset, int n, int m, bool[] removed, bool[] added)
    {
        if (n + m > MaxDiffedLines)
        {
            return false;
        }

        if (n == 0 || m == 0)
        {
            // Pure insertion or deletion; no search needed.
            for (int i = 0; i < n; i++)
            {
                removed[offset + i] = true;
            }

            for (int i = 0; i < m; i++)
            {
                added[offset + i] = true;
            }

            return true;
        }

        int max = Math.Min(n + m, MaxEditDistance);
        var trace = new List<int[]>();
        int[] v = new int[2 * max + 1];
        int found = -1;

        for (int d = 0; d <= max && found < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int index = k + max;
                int x = k == -d || (k != d && v[index - 1] < v[index + 1])
                    ? v[index + 1]
                    : v[index - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[offset + x] == b[offset + y])
                {
                    x++;
                    y++;
                }

                v[index] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }

            trace.Add((int[])v.Clone());
        }

        if (found < 0)
        {
            return false;
        }

        // Backtrack: walk the trace from the end, marking non-diagonal steps.
        int px = n;
        int py = m;
        for (int d = found; d > 0; d--)
        {
            int[] prev = trace[d - 1];
            int k = px - py;
            int index = k + max;
            bool down = k == -d || (k != d && prev[index - 1] < prev[index + 1]);
            int prevK = down ? k + 1 : k - 1;
            int prevX = prev[prevK + max];
            int prevY = prevX - prevK;

            // Follow the diagonal back to the step.
            while (px > prevX && py > prevY)
            {
                px--;
                py--;
            }

            if (down)
            {
                added[offset + prevY] = true; // insertion of b[prevY]
            }
            else
            {
                removed[offset + prevX] = true; // deletion of a[prevX]
            }

            px = prevX;
            py = prevY;
        }

        return true;
    }

    /// <summary>Groups marked lines into unified hunks with <see cref="ContextLines"/> context.</summary>
    private static List<PatchHunk> BuildHunks(string[] a, string[] b, bool[] removed, bool[] added)
    {
        var hunks = new List<PatchHunk>();
        int ai = 0;
        int bi = 0;

        while (ai < a.Length || bi < b.Length)
        {
            // Advance over the unchanged region.
            while (ai < a.Length && !removed[ai] && (bi >= b.Length || !added[bi]))
            {
                if (bi < b.Length)
                {
                    bi++;
                }

                ai++;
            }

            if (ai >= a.Length && bi >= b.Length)
            {
                break;
            }

            // A change starts here; open the hunk ContextLines earlier.
            int hunkA = Math.Max(0, ai - ContextLines);
            int hunkB = Math.Max(0, bi - ContextLines);
            var body = new StringBuilder();
            for (int c = hunkA; c < ai; c++)
            {
                body.Append(' ').Append(a[c]).Append('\n');
            }

            int endA = ai;
            int endB = bi;
            int quiet = 0;
            while ((endA < a.Length || endB < b.Length) && quiet <= 2 * ContextLines)
            {
                if (endA < a.Length && removed[endA])
                {
                    FlushQuiet(body, a, endA, ref quiet);
                    body.Append('-').Append(a[endA]).Append('\n');
                    endA++;
                }
                else if (endB < b.Length && added[endB])
                {
                    FlushQuiet(body, a, endA, ref quiet);
                    body.Append('+').Append(b[endB]).Append('\n');
                    endB++;
                }
                else if (endA < a.Length && endB < b.Length)
                {
                    // Unchanged line: buffered as trailing context / gap filler.
                    quiet++;
                    endA++;
                    endB++;
                }
                else
                {
                    break;
                }
            }

            // Trailing quiet lines beyond ContextLines belong to the next gap.
            int trailing = Math.Min(quiet, ContextLines);
            int giveBack = quiet - trailing;
            endA -= giveBack;
            endB -= giveBack;
            for (int c = endA - trailing; c < endA; c++)
            {
                body.Append(' ').Append(a[c]).Append('\n');
            }

            int oldStart = Math.Min(hunkA + 1, a.Length);
            int newStart = Math.Min(hunkB + 1, b.Length);
            hunks.Add(new PatchHunk(
                oldStart, endA - hunkA, newStart, endB - hunkB,
                body.ToString().TrimEnd('\n')));

            ai = endA;
            bi = endB;
        }

        return hunks;
    }

    /// <summary>Turns buffered quiet lines into in-hunk context once another change follows.</summary>
    private static void FlushQuiet(StringBuilder body, string[] a, int endA, ref int quiet)
    {
        for (int c = endA - quiet; c < endA; c++)
        {
            body.Append(' ').Append(a[c]).Append('\n');
        }

        quiet = 0;
    }
}
