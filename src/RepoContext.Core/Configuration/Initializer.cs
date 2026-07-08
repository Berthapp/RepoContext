namespace RepoContext.Core.Configuration;

/// <summary>The outcome of <c>repoctx init</c>.</summary>
public sealed record InitResult
{
    public required bool ConfigCreated { get; init; }

    public required bool ConfigOverwritten { get; init; }

    public required bool GitignoreUpdated { get; init; }

    public required string ConfigPath { get; init; }
}

/// <summary>Initializes a repository for RepoContext (spec F1).</summary>
public static class Initializer
{
    /// <summary>
    /// Creates <c>.repoctx/</c>, writes the default <c>repoctx.config.json</c>,
    /// and ensures <c>.gitignore</c> excludes the index directory.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when already initialized and <paramref name="force"/> is false.
    /// </exception>
    public static InitResult Initialize(RepoLayout layout, bool force)
    {
        bool alreadyInitialized = File.Exists(layout.ConfigPath);
        if (alreadyInitialized && !force)
        {
            throw new InvalidOperationException(
                $"Already initialized ({RepoContextInfo.ConfigFileName} exists). Use --force to overwrite.");
        }

        Directory.CreateDirectory(layout.IndexDirectory);
        ConfigStore.Save(layout.ConfigPath, RepoctxConfig.CreateDefault());

        bool gitignoreUpdated = EnsureGitignore(layout.Root);

        return new InitResult
        {
            ConfigCreated = !alreadyInitialized,
            ConfigOverwritten = alreadyInitialized,
            GitignoreUpdated = gitignoreUpdated,
            ConfigPath = layout.ConfigPath,
        };
    }

    private static bool EnsureGitignore(string root)
    {
        string path = Path.Combine(root, ".gitignore");
        const string entry = ".repoctx/";

        if (!File.Exists(path))
        {
            File.WriteAllText(path, entry + "\n");
            return true;
        }

        string[] lines = File.ReadAllLines(path);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed is entry or ".repoctx" or "/.repoctx" or "/.repoctx/")
            {
                return false;
            }
        }

        string existing = File.ReadAllText(path);
        string prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
        File.AppendAllText(path, prefix + "\n# RepoContext index (machine-local, sensitive)\n" + entry + "\n");
        return true;
    }
}
