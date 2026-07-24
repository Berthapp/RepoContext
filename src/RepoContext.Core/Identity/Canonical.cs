using System.Security.Cryptography;
using System.Text;

namespace RepoContext.Core.Identity;

/// <summary>
/// Centralised deterministic canonical hashing (Q4, ADR 0015). Every fingerprint
/// and receipt in the system is a SHA-256 over a canonical byte layout produced
/// here, so the layout is defined in exactly one place and can be documented and
/// tested. Every field and repeated record is UTF-8 length-prefixed, so arbitrary
/// query text and legal file names cannot make concatenation ambiguous.
/// </summary>
/// <remarks>
/// Determinism rules enforced by construction: fields are hashed in the caller's
/// declared order, paths must be slash-normalised and ordinally sorted by the
/// caller before entry, and no timestamps, absolute paths, process IDs, random
/// values, or hash-map iteration order ever reach this method.
/// </remarks>
public static class Canonical
{
    /// <summary>
    /// SHA-256 over the ordered fields, returned as lowercase hex. Each field is
    /// encoded as <c>decimal UTF-8 byte length ':' UTF-8 bytes</c>.
    /// </summary>
    public static string Hash(params ReadOnlySpan<string> fields)
    {
        var sb = new StringBuilder();
        foreach (string field in fields)
        {
            AppendLengthPrefixed(sb, field);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>SHA-256 over raw UTF-8 bytes (already-canonical text), lowercase hex.</summary>
    public static string HashUtf8(string canonicalText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));

    /// <summary>Slash-normalises a path so separators cannot vary the hash across platforms.</summary>
    public static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Encodes pre-sorted entries as a sequence of UTF-8 length-prefixed records.
    /// Unlike a delimiter, this remains unambiguous when a record contains control
    /// characters or delimiter-looking text.
    /// </summary>
    public static string JoinRecords(IEnumerable<string> orderedRecords)
    {
        var sb = new StringBuilder();
        foreach (string record in orderedRecords)
        {
            AppendLengthPrefixed(sb, record);
        }

        return sb.ToString();
    }

    private static void AppendLengthPrefixed(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':').Append(value);
    }
}
