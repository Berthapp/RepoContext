using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepoContext.Core.Stats;

/// <summary>
/// The local usage log behind <c>repoctx stats</c> (ADR 0011): one JSON line
/// per recorded query, appended to <c>.repoctx/stats.jsonl</c>. Local-only —
/// the file lives in the ignored index directory and never leaves the machine.
/// </summary>
public static class UsageLog
{
    private const int AppendLockTimeoutMilliseconds = 3_000;

    /// <summary>The log file name inside the index directory.</summary>
    public const string FileName = "stats.jsonl";

    /// <summary>The usage log path for a repository.</summary>
    public static string PathFor(RepoLayout layout) =>
        Path.Combine(layout.IndexDirectory, FileName);

    /// <summary>
    /// Appends one record as a single JSON line. Writers are serialized across
    /// processes, while readers may keep the log open. If a crashed writer left
    /// an unterminated tail, it is separated before the new record is written.
    /// </summary>
    public static void Append(string path, UsageRecord record)
    {
        if (!IsValid(record))
        {
            throw new ArgumentException("The usage record contains invalid values.", nameof(record));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] line = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(record, UsageRecord.SerializerOptions) + "\n");

        using var mutex = new Mutex(initiallyOwned: false, MutexName(path));
        bool ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(AppendLockTimeoutMilliseconds);
                if (!ownsMutex)
                {
                    throw new TimeoutException("Timed out waiting to append usage statistics.");
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous writer crashed. The OS transferred ownership to
                // this thread; the tail repair below makes its partial write safe.
                ownsMutex = true;
            }

            using var stream = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n')
                {
                    stream.WriteByte((byte)'\n');
                }
            }

            stream.Write(line);
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
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
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                UsageRecord? record = JsonSerializer.Deserialize<UsageRecord>(
                    line, UsageRecord.SerializerOptions);
                if (record is not null && IsValid(record))
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

    private static bool IsValid(UsageRecord record) =>
        record.V == 1 &&
        !string.IsNullOrWhiteSpace(record.Command) &&
        record.Source is UsageSources.Cli or UsageSources.Mcp &&
        record.Served >= 0 &&
        record.Replaced >= 0 &&
        record.Files is null or >= 0 &&
        record.Unchanged is null or >= 0 &&
        (record.Unchanged is null ||
            record.Unchanged is { } unchanged &&
            record.Files is { } files &&
            unchanged <= files);

    /// <summary>
    /// A stable, path-scoped mutex name lets independent repoctx processes
    /// coordinate without leaving a lock file behind. Windows paths are folded
    /// because its file system is case-insensitive by default.
    /// </summary>
    private static string MutexName(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            fullPath = fullPath.ToUpperInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        return "RepoContext.Stats." + Convert.ToHexString(hash);
    }
}
