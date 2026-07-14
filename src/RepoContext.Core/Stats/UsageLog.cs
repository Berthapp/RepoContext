using System.Text.Json;

namespace RepoContext.Core.Stats;

/// <summary>
/// The local usage log behind <c>repoctx stats</c> (ADR 0011): one JSON line
/// per recorded query, appended to <c>.repoctx/stats.jsonl</c>. Local-only —
/// the file lives in the ignored index directory and never leaves the machine.
/// </summary>
public static class UsageLog
{
    /// <summary>The log file name inside the index directory.</summary>
    public const string FileName = "stats.jsonl";

    /// <summary>The usage log path for a repository.</summary>
    public static string PathFor(RepoLayout layout) =>
        Path.Combine(layout.IndexDirectory, FileName);

    /// <summary>Appends one record as a single JSON line.</summary>
    public static void Append(string path, UsageRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string line = JsonSerializer.Serialize(record, UsageRecord.SerializerOptions) + "\n";

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.Write(line);
    }

    /// <summary>
    /// Reads all records. Tolerant by design: a missing file is an empty log,
    /// and malformed lines (crashed writer, foreign content) are skipped so a
    /// damaged log can never break the dashboard.
    /// </summary>
    public static IReadOnlyList<UsageRecord> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var records = new List<UsageRecord>();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                UsageRecord? record = JsonSerializer.Deserialize<UsageRecord>(
                    line, UsageRecord.SerializerOptions);
                if (record is { Command.Length: > 0, Source.Length: > 0 })
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        return records;
    }
}
