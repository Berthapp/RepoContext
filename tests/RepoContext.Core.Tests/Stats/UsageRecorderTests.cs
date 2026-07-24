using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
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
                rendered: "{\"schema_version\":3}", replacedTokens: 1234, files: 2, unchanged: 1);

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

    [Fact]
    public void Record_AcceptsReuseBeyondDeliveredFiles_AndAppliesScale()
    {
        RepoLayout layout = RepoLayout.For(_dir);
        string? previous = Environment.GetEnvironmentVariable(UsageRecorder.DisableVariable);
        try
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, null);
            UsageRecorder.Record(
                layout, "context", UsageSources.Cli, rendered: "compact response",
                files: 1, unchanged: 3,
                scale: TokenScale.From(
                    RepoctxConfig.CreateDefault() with
                    {
                        Tokens = new TokenOptions { Factor = 2.0 },
                    }));

            UsageRecord record = Assert.Single(
                UsageLog.Read(UsageLog.PathFor(layout)));
            Assert.Equal(1, record.Files);
            Assert.Equal(3, record.Unchanged);
            Assert.Equal(2 * Tokens.Count("compact response"), record.Served);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                UsageRecorder.DisableVariable, previous);
        }
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
