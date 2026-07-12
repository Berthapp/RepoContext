namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests that exercise the <c>repoctx</c> CLI as a real process and
/// assert its version, help surface and F7 exit-code contract.
/// </summary>
public class CliContractTests
{
    [Fact]
    public void Version_PrintsVersionAndSucceeds()
    {
        CliResult result = CliHarness.Run("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Cli.CliInfo.Version, result.StdOut);
    }

    [Fact]
    public void Help_ListsEveryCommandAndSucceeds()
    {
        CliResult result = CliHarness.Run("--help");

        Assert.Equal(0, result.ExitCode);
        foreach (string command in new[] { "init", "index", "search", "related", "context", "architecture" })
        {
            Assert.Contains(command, result.StdOut);
        }
    }

    [Fact]
    public void UnknownCommand_ReturnsInvalidArguments()
    {
        CliResult result = CliHarness.Run("definitely-not-a-command");

        Assert.Equal(3, result.ExitCode);
    }
}
