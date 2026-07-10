using System.Reflection;

namespace RepoContext.Cli;

/// <summary>Assembly metadata for the CLI.</summary>
public static class CliInfo
{
    /// <summary>The tool version (e.g. <c>0.1.0</c>), recorded in the index meta.</summary>
    public static string Version { get; } =
        typeof(CliInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(CliInfo).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
}
