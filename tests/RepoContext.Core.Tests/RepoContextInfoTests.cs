using Microsoft.Data.Sqlite;
using RepoContext.Core;

namespace RepoContext.Core.Tests;

public class RepoContextInfoTests
{
    [Fact]
    public void SchemaVersion_IsOne()
    {
        Assert.Equal(1, RepoContextInfo.SchemaVersion);
    }

    [Fact]
    public void IndexDirectoryName_IsRepoctxDot()
    {
        Assert.Equal(".repoctx", RepoContextInfo.IndexDirectoryName);
    }

    /// <summary>
    /// Guards the pinned <c>SQLitePCLRaw.bundle_e_sqlite3</c> 3.x override (ADR 0002):
    /// verifies the native provider actually loads and FTS5 is available, since a
    /// major-version bump of the native bundle could otherwise break at runtime.
    /// </summary>
    [Fact]
    public void Sqlite_NativeBundle_LoadsAndSupportsFts5()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE t USING fts5(content);";
        command.ExecuteNonQuery();

        command.CommandText = "INSERT INTO t(content) VALUES ('hello world');";
        command.ExecuteNonQuery();

        command.CommandText = "SELECT count(*) FROM t WHERE t MATCH 'world';";
        var matches = Convert.ToInt64(command.ExecuteScalar());

        Assert.Equal(1, matches);
    }
}
