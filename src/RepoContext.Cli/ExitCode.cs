namespace RepoContext.Cli;

/// <summary>
/// Process exit codes for the <c>repoctx</c> CLI. These form part of the public
/// tool contract (spec F7) and must remain stable.
/// </summary>
public static class ExitCode
{
    /// <summary>Command completed successfully.</summary>
    public const int Success = 0;

    /// <summary>A general error occurred while executing the command.</summary>
    public const int Error = 1;

    /// <summary>No index was found; the repository has not been indexed yet.</summary>
    public const int NoIndex = 2;

    /// <summary>The command was invoked with invalid arguments.</summary>
    public const int InvalidArguments = 3;
}
