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
        string command, int served, int replaced, int? files = null, int? unchanged = null) => new()
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
