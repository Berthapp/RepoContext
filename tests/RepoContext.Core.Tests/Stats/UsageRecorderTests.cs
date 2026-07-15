using RepoContext.Core;
using RepoContext.Core.Stats;

namespace RepoContext.Core.Tests.Stats;

public class UsageRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "repoctx-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Record_AppendsARecordWithRealTokenCounts()
    {
        RepoLayout layout = RepoLayout.For(_dir);
        string? previous = Environment.GetEnvironmentVariable(UsageRecorder.DisableVariable);

        try
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, null);
            UsageRecorder.Record(layout, "context", UsageSources.Cli,
                rendered: "{\"schema_version\":2}", replacedTokens: 1234, files: 2, unchanged: 1);

            IReadOnlyList<UsageRecord> records = UsageLog.Read(UsageLog.PathFor(layout));
            UsageRecord record = Assert.Single(records);
            Assert.Equal("context", record.Command);
            Assert.Equal(UsageSources.Cli, record.Source);
            Assert.True(record.Served > 0);
            Assert.Equal(1234, record.Replaced);
            Assert.Equal(2, record.Files);
            Assert.Equal(1, record.Unchanged);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, previous);
        }
    }

    [Fact]
    public void Record_IsDisabledByEnvironmentVariable()
    {
        RepoLayout layout = RepoLayout.For(_dir);
        string? previous = Environment.GetEnvironmentVariable(UsageRecorder.DisableVariable);
        try
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, "1");
            UsageRecorder.Record(layout, "search", UsageSources.Cli, rendered: "hits");
        }
        finally
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, previous);
        }

        Assert.False(File.Exists(UsageLog.PathFor(layout)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Covers DirectoryNotFoundException when recording was disabled.
        }
    }
}
