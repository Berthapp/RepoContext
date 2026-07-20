using System.Security.Cryptography;
using System.Text;

namespace RepoContext.Core.Memory;

/// <summary>
/// The kinds of agent memory (ADR 0013). The tool never authors these — an
/// agent writes them and RepoContext stores, retrieves and stale-flags them
/// deterministically, so the no-LLM constraint holds.
/// </summary>
public static class MemoryKinds
{
    /// <summary>Distilled knowledge about the code ("PaymentService retries 3x with backoff").</summary>
    public const string Note = "note";

    /// <summary>A recorded decision with its why ("JWT over cookies: mobile clients").</summary>
    public const string Decision = "decision";

    /// <summary>An invariant or warning ("do not rename Action — public API").</summary>
    public const string Constraint = "constraint";

    /// <summary>All valid kinds, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = [Note, Decision, Constraint];

    /// <summary>Whether <paramref name="kind"/> is one of the known kinds.</summary>
    public static bool IsValid(string? kind) => kind is Note or Decision or Constraint;
}

/// <summary>
/// One agent-authored memory entry. Long-term entries (<see cref="Session"/>
/// null) persist across sessions; session-scoped entries are the short-term
/// scratchpad of one <c>--session</c> and surface only with it.
/// </summary>
public sealed record MemoryEntry
{
    /// <summary>Content-addressed id: re-adding identical content updates in place.</summary>
    public required string Id { get; init; }

    /// <summary>One of <see cref="MemoryKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The agent's distilled text, capped so memories stay cheaper than reads.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Linked repo-relative paths → short content hash at write time. The
    /// hashes make staleness detectable: when a linked file's indexed hash
    /// drifts, the memory is flagged stale instead of silently rotting.
    /// </summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Optional lowercase tags for recall.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Session scope; null means long-term (visible to every call).</summary>
    public string? Session { get; init; }

    /// <summary>
    /// UTC date the entry was written (<c>yyyy-MM-dd</c>). Stored at write time
    /// and echoed verbatim, so query output stays deterministic for a given store.
    /// </summary>
    public required string Created { get; init; }

    /// <summary>Ids are 8 hex characters — visually distinct from 12-hex content hashes.</summary>
    public const int IdLength = 8;

    /// <summary>
    /// Computes the content-addressed id over the fields that define identity
    /// (kind, text, session scope and the linked path set — not the hashes,
    /// which refresh on update).
    /// </summary>
    public static string ComputeId(string kind, string text, string? session, IEnumerable<string> files)
    {
        // Unit-separator joins keep the material unambiguous even when the
        // text itself contains newlines.
        string material = string.Join('\u001f',
            kind, text, session ?? string.Empty, string.Join('\u001f', files.Order(StringComparer.Ordinal)));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash)[..IdLength];
    }
}
