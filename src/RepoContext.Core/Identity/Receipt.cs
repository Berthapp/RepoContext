namespace RepoContext.Core.Identity;

/// <summary>What kind of evidence unit a receipt covers.</summary>
public enum EvidenceUnitKind
{
    /// <summary>A pointer to a file, with no content delivered.</summary>
    Pointer,

    /// <summary>An exact source line span whose text was delivered.</summary>
    Span,

    /// <summary>One outline symbol whose signature and doc summary were delivered.</summary>
    Symbol,
}

/// <summary>
/// A stateless, opaque possession proof for exactly one delivered evidence unit
/// (Q1, ADR 0015).
/// </summary>
/// <remarks>
/// <para>
/// This replaces the unsafe pre-Release-1 practice of echoing a <i>file</i> hash
/// to suppress a <i>partial</i> response. A file hash proves which version of a
/// file was indexed; it never proved that the caller received a particular range
/// or outline, so echoing the hash returned alongside a slice could suppress
/// lines the caller had never seen.
/// </para>
/// <para>
/// A receipt binds the exact unit: its path, the full content hash of the file it
/// came from, the detail kind, the exact range or symbol identity, the canonical
/// delivered text, and the producer versions that shaped it. It deliberately does
/// <b>not</b> include ranking or any global analysis state, so an unrelated file
/// changing elsewhere in the repository cannot invalidate evidence the caller
/// still legitimately holds.
/// </para>
/// <para>
/// There is no receipt database, prefix expansion, or history-dependent lookup:
/// the engine recomputes candidate receipts from current evidence and compares
/// for equality. Because the value is a pure function of the unit, the same unit
/// copied into another repository yields the same receipt — that is safe by
/// construction, so "foreign repository" is not a failure class.
/// </para>
/// </remarks>
public static class Receipt
{
    /// <summary>Canonical layout version; a bump invalidates every outstanding receipt.</summary>
    public const string Version = "r1";

    /// <summary>Length of the encoded value: SHA-256 as unpadded base64url.</summary>
    public const int EncodedLength = 43;

    /// <summary>
    /// Computes the receipt for one evidence unit.
    /// </summary>
    /// <param name="path">Repo-relative path of the source file.</param>
    /// <param name="fullContentHash">Full (untruncated) content hash of that file.</param>
    /// <param name="detail">The detail level the unit was produced for.</param>
    /// <param name="kind">Pointer, span or symbol.</param>
    /// <param name="startLine">First line of the unit (1-based, inclusive); 0 for a pointer.</param>
    /// <param name="endLine">Last line of the unit (inclusive); 0 for a pointer.</param>
    /// <param name="symbolIdentity">Symbol name for symbol units; empty otherwise.</param>
    /// <param name="deliveredEvidence">The exact canonical text handed to the caller.</param>
    public static string For(
        string path, string fullContentHash, string detail, EvidenceUnitKind kind,
        int startLine, int endLine, string symbolIdentity, string deliveredEvidence)
    {
        string digest = Canonical.Hash(
            Version,
            Canonical.NormalizePath(path),
            fullContentHash,
            detail,
            kind.ToString().ToLowerInvariant(),
            Format(startLine),
            Format(endLine),
            symbolIdentity,
            Canonical.HashUtf8(deliveredEvidence),
            RelevantProducer(kind));

        return Encode(digest);
    }

    /// <summary>
    /// Whether a caller-supplied value could be a receipt at all. Malformed input
    /// fails closed here and is reported as invalid rather than silently matching
    /// nothing (which would look identical to "not yet seen").
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != EncodedLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Hex digest to unpadded base64url, so the token stays short and URL/CLI safe.</summary>
    private static string Encode(string hexDigest)
    {
        byte[] bytes = Convert.FromHexString(hexDigest);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Format(int line) =>
        line.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Only transformations capable of changing this unit belong in its
    /// receipt. A graph or tokenizer bump must not invalidate identical source
    /// evidence and force an agent to pay for it again.
    /// </summary>
    private static string RelevantProducer(EvidenceUnitKind kind) => kind switch
    {
        EvidenceUnitKind.Symbol =>
            $"dec{ProducerVersions.Decoder}.par{ProducerVersions.Parser}.{ProducerVersions.EvidenceVersion}",
        EvidenceUnitKind.Span =>
            $"dec{ProducerVersions.Decoder}.{ProducerVersions.EvidenceVersion}",
        _ => "pointer.v1",
    };
}
