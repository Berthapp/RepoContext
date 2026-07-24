using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace RepoContext.Integration.Tests;

/// <summary>
/// Executes the shipped MSBuild targets against a synthetic consumer. This
/// catches portability regressions in the generated MCP launch command without
/// requiring a network restore or installing the package in the test process.
/// </summary>
public sealed class MSBuildPackageTests
{
    [Fact]
    public void McpSetup_CopiesPortablePayload_AndGeneratesCrossPlatformCommand()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "repoctx-msbuild-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string firstSource = CreatePayload(root, "package-v1", "first-version");
            string project = CreateConsumerProject(root);

            RunTarget(project, root, firstSource, "RepoCtxMcpConfig");

            string localAssembly = Path.Combine(
                root, ".repoctx", "bin", "tool", "repoctx.dll");
            Assert.Equal("first-version", File.ReadAllText(localAssembly));

            string configPath = Path.Combine(root, ".vscode", "mcp.json");
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath)))
            {
                JsonElement server = document.RootElement
                    .GetProperty("servers")
                    .GetProperty("repoctx");
                Assert.Equal("stdio", server.GetProperty("type").GetString());
                Assert.Equal("dotnet", server.GetProperty("command").GetString());
                Assert.Equal(
                    ["exec", "${workspaceFolder}/.repoctx/bin/tool/repoctx.dll", "mcp"],
                    server.GetProperty("args")
                        .EnumerateArray()
                        .Select(value => value.GetString() ?? string.Empty)
                        .ToArray());
                Assert.Equal("${workspaceFolder}", server.GetProperty("cwd").GetString());
            }

            // A new immutable package path invalidates the one-line stamp and
            // refreshes the stable workspace payload.
            string secondSource = CreatePayload(root, "package-v2", "second-version");
            RunTarget(project, root, secondSource, "RepoCtxInstall");
            Assert.Equal("second-version", File.ReadAllText(localAssembly));

            RunTarget(project, root, secondSource, "RepoCtxShim");
            string shim = File.ReadAllText(Path.Combine(root, ".repoctx", "bin", "repoctx"));
            string cmd = File.ReadAllText(Path.Combine(root, ".repoctx", "bin", "repoctx.cmd"));
            Assert.Contains(localAssembly, shim, StringComparison.Ordinal);
            Assert.Contains(localAssembly, cmd, StringComparison.Ordinal);
            Assert.DoesNotContain(secondSource, shim, StringComparison.Ordinal);
            Assert.DoesNotContain(secondSource, cmd, StringComparison.Ordinal);

            // Existing MCP configuration is user-owned and must not be merged
            // or overwritten by a later package build.
            const string customConfig = """{"servers":{"custom":{"command":"custom"}}}""";
            File.WriteAllText(configPath, customConfig);
            RunTarget(project, root, secondSource, "RepoCtxMcpConfig");
            Assert.Equal(customConfig, File.ReadAllText(configPath));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string CreatePayload(string root, string name, string content)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(directory, "runtimes", "test", "native"));
        string assembly = Path.Combine(directory, "repoctx.dll");
        File.WriteAllText(assembly, content);
        File.WriteAllText(
            Path.Combine(directory, "runtimes", "test", "native", "dependency.bin"),
            name);
        return assembly;
    }

    private static string CreateConsumerProject(string root)
    {
        string targets = Path.Combine(
            FindRepoRoot(), "src", "RepoContext.MSBuild", "build",
            "RepoContext.MSBuild.targets");
        string project = Path.Combine(root, "Consumer.proj");
        new XDocument(
            new XElement(
                "Project",
                new XElement("Import", new XAttribute("Project", targets))))
            .Save(project);
        return project;
    }

    private static void RunTarget(
        string project,
        string root,
        string sourceAssembly,
        string target)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add($"-t:{target}");
        startInfo.ArgumentList.Add($"-p:RepoCtxRoot={root}");
        startInfo.ArgumentList.Add(
            $"-p:RepoCtxToolsDirectory={Path.GetDirectoryName(sourceAssembly)}" +
            Path.DirectorySeparatorChar);
        startInfo.ArgumentList.Add($"-p:RepoCtxToolAssembly={sourceAssembly}");
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-v:minimal");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(milliseconds: 30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The RepoContext.MSBuild target did not finish in 30 seconds.");
        }

        Assert.True(
            process.ExitCode == 0,
            $"MSBuild target {target} failed with exit code {process.ExitCode}.{Environment.NewLine}" +
            stdout + stderr);
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RepoContext.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
