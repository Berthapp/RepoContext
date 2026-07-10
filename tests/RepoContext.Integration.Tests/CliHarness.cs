using System.Diagnostics;
using System.Text;

namespace RepoContext.Integration.Tests;

/// <summary>
/// Result of running the <c>repoctx</c> CLI as a child process.
/// </summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>Combined stdout + stderr, convenient for assertions.</summary>
    public string Output => StdOut + StdErr;
}

/// <summary>
/// Locates the freshly built <c>repoctx</c> binary and runs it end-to-end.
/// </summary>
/// <remarks>
/// The integration project references RepoContext.Cli, so the binary is always
/// built before these tests run. We invoke it through <c>dotnet repoctx.dll</c>
/// so the test works identically on every OS and in the no-network CI job.
/// </remarks>
public static class CliHarness
{
    private static readonly string CliDll = LocateCliDll();

    /// <summary>Absolute path to the built <c>repoctx.dll</c> (e.g. to spawn <c>repoctx mcp</c>).</summary>
    public static string CliDllPath => CliDll;

    public static CliResult Run(params string[] args) =>
        RunIn(Environment.CurrentDirectory, args);

    public static CliResult RunIn(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        startInfo.ArgumentList.Add(CliDll);
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(milliseconds: 60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"repoctx {string.Join(' ', args)} did not exit within 60s.");
        }

        process.WaitForExit(); // ensure async output is flushed
        return new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string LocateCliDll()
    {
        string baseDir = AppContext.BaseDirectory;
        string configuration = baseDir.Replace('\\', '/').Contains("/Release/", StringComparison.Ordinal)
            ? "Release"
            : "Debug";

        string repoRoot = FindRepoRoot(baseDir);
        string dll = Path.Combine(
            repoRoot, "src", "RepoContext.Cli", "bin", configuration, "net10.0", "repoctx.dll");

        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"Could not find the built repoctx binary at '{dll}'. " +
                "Build the solution before running the integration tests.");
        }

        return dll;
    }

    private static string FindRepoRoot(string startDirectory)
    {
        for (DirectoryInfo? dir = new(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RepoContext.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "RepoContext.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (RepoContext.slnx) from '{startDirectory}'.");
    }
}
