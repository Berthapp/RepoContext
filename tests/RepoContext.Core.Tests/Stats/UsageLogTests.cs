using System.Text.Json;
using RepoContext.Core.Stats;

namespace RepoContext.Core.Tests.Stats;

public class UsageLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "repoctx-tests", Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, ".repoctx", UsageLog.FileName);

    [Fact]
    public void Append_ThenRead_RoundTripsRecords()
    {
        UsageLog.Append(LogPath, Record("context", served: 2110, replaced: 5336, files: 3, unchanged: 1));
        UsageLog.Append(LogPath, Record("search", served: 240, replaced: 0));

        IReadOnlyList<UsageRecord> records = UsageLog.Read(LogPath);

        Assert.Equal(2, records.Count);
        Assert.Equal("context", records[0].Command);
        Assert.Equal(UsageSources.Cli, records[0].Source);
        Assert.Equal(2110, records[0].Served);
        Assert.Equal(5336, records[0].Replaced);
        Assert.Equal(3, records[0].Files);
        Assert.Equal(1, records[0].Unchanged);
        Assert.Equal("search", records[1].Command);
        Assert.Null(records[1].Files);
    }

    [Fact]
    public void Read_MissingFile_IsEmpty() =>
        Assert.Empty(UsageLog.Read(LogPath));

    [Fact]
    public void Read_SkipsMalformedAndBlankLines()
    {
        UsageLog.Append(LogPath, Record("outline", served: 100, replaced: 300));
        File.AppendAllText(LogPath, "not json at all\n\n{\"v\":1}\n");
        UsageLog.Append(LogPath, Record("search", served: 50, replaced: 0));

        IReadOnlyList<UsageRecord> records = UsageLog.Read(LogPath);

        Assert.Equal(2, records.Count);
        Assert.Equal(["outline", "search"], records.Select(r => r.Command));
    }

    [Fact]
    public void Append_ConcurrentWriters_RetainsEveryRecord()
    {
        const int recordCount = 64;

        Parallel.For(0, recordCount, i =>
            UsageLog.Append(LogPath, Record("search", served: i, replaced: 0)));

        IReadOnlyList<UsageRecord> records = UsageLog.Read(LogPath);

        Assert.Equal(recordCount, records.Count);
        Assert.Equal(Enumerable.Range(0, recordCount), records.Select(r => r.Served).Order());
    }

    [Fact]
    public void Read_WhileWriterHasLogOpen_DoesNotThrow()
    {
        UsageLog.Append(LogPath, Record("context", served: 100, replaced: 300));
        using var writer = new FileStream(
            LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer.WriteByte((byte)'{');
        writer.Flush();

        IReadOnlyList<UsageRecord> records = UsageLog.Read(LogPath);

        Assert.Single(records);
        Assert.Equal("context", records[0].Command);
    }

    [Fact]
    public void Append_AfterPartialTail_SeparatesAndPreservesNewRecord()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, "{\"v\":1");

        UsageLog.Append(LogPath, Record("search", served: 50, replaced: 0));

        IReadOnlyList<UsageRecord> records = UsageLog.Read(LogPath);
        Assert.Single(records);
        Assert.Equal("search", records[0].Command);
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void Read_SkipsUnsupportedAndInvalidRecords()
    {
        UsageRecord valid = Record("search", served: 50, replaced: 0);
        UsageRecord[] invalid =
        [
            valid with { V = 2 },
            valid with { Command = " " },
            valid with { Source = "foreign" },
            valid with { Served = -1 },
            valid with { Replaced = -1 },
            valid with { Files = -1 },
            valid with { Unchanged = -1 },
        ];
        string validJson = JsonSerializer.Serialize(valid, UsageRecord.SerializerOptions);
        string withoutVersion = validJson.Replace("\"v\":1,", string.Empty, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, string.Join('\n', invalid.Select(record =>
            JsonSerializer.Serialize(record, UsageRecord.SerializerOptions)).Append(withoutVersion)) + "\n");
        UsageLog.Append(LogPath, valid);

        IReadOnlyList<UsageRecord> records = UsageLog.Read(LogPath);

        Assert.Single(records);
        Assert.Equal(valid, records[0]);
    }

    [Fact]
    public void Read_AcceptsMoreReusedFilesThanDeliveredFiles()
    {
        UsageRecord reuseHeavy = Record(
            "context", served: 50, replaced: 0, files: 1, unchanged: 3);

        UsageLog.Append(LogPath, reuseHeavy);

        Assert.Equal(reuseHeavy, Assert.Single(UsageLog.Read(LogPath)));
    }

    [Fact]
    public void Append_RejectsInvalidRecord()
    {
        UsageRecord invalid = Record("search", served: -1, replaced: 0);

        Assert.Throws<ArgumentException>(() => UsageLog.Append(LogPath, invalid));
        Assert.False(File.Exists(LogPath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static UsageRecord Record(
        string command, int served, int replaced, int? files = null, int? unchanged = null) =>
        new()
        {
            Ts = new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero),
            Command = command,
            Source = UsageSources.Cli,
            Served = served,
            Replaced = replaced,
            Files = files,
            Unchanged = unchanged,
        };
}
