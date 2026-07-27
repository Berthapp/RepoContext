namespace RepoContext.Core.Scanning;

/// <summary>A project root discovered inside the repository.</summary>
public sealed record DetectedProject
{
    /// <summary>Repo-relative directory, <c>.</c> for the repository root.</summary>
    public required string Path { get; init; }

    /// <summary>The marker file that identified the project.</summary>
    public required string Marker { get; init; }

    /// <summary>Coarse ecosystem name, e.g. <c>node</c> or <c>dotnet</c>.</summary>
    public required string Ecosystem { get; init; }
}

/// <summary>
/// Identifies project roots from marker files among the scanned files. A
/// repository may hold many - a folder with two checkouts, a monorepo with
/// packages, a solution with several project files - and every one of them is
/// indexed, so this is purely informational: it lets <c>init</c> report the
/// coverage a user is getting. Deterministic: derived from the already-sorted
/// scan result, one project per directory, ordered by path.
/// </summary>
public static class ProjectDetector
{
    /// <summary>
    /// Marker file names in priority order. The first match in a directory wins
    /// so a directory holding both <c>package.json</c> and a lockfile-adjacent
    /// marker is reported once.
    /// </summary>
    private static readonly (string Name, string Ecosystem)[] NameMarkers =
    [
        ("package.json", "node"),
        ("go.mod", "go"),
        ("Cargo.toml", "rust"),
        ("pyproject.toml", "python"),
        ("setup.py", "python"),
        ("requirements.txt", "python"),
        ("composer.json", "php"),
        ("Gemfile", "ruby"),
        ("pom.xml", "java"),
        ("build.gradle", "java"),
        ("build.gradle.kts", "java"),
        ("CMakeLists.txt", "cmake"),
    ];

    private static readonly (string Extension, string Ecosystem)[] ExtensionMarkers =
    [
        (".csproj", "dotnet"),
        (".fsproj", "dotnet"),
        (".vbproj", "dotnet"),
    ];

    /// <summary>Detects project roots among <paramref name="files"/>.</summary>
    public static IReadOnlyList<DetectedProject> Detect(IEnumerable<ScannedFile> files)
    {
        var byDirectory = new Dictionary<string, (int Rank, DetectedProject Project)>(StringComparer.Ordinal);

        foreach (ScannedFile file in files)
        {
            string name = System.IO.Path.GetFileName(file.RelativePath);
            (string Ecosystem, int Rank)? marker = Classify(name);
            if (marker is not { } found)
            {
                continue;
            }

            string directory = Directory(file.RelativePath);
            if (byDirectory.TryGetValue(directory, out (int Rank, DetectedProject Project) existing)
                && existing.Rank <= found.Rank)
            {
                continue;
            }

            byDirectory[directory] = (found.Rank, new DetectedProject
            {
                Path = directory,
                Marker = name,
                Ecosystem = found.Ecosystem,
            });
        }

        return [.. byDirectory.Values
            .Select(entry => entry.Project)
            .OrderBy(project => project.Path, StringComparer.Ordinal)];
    }

    private static (string Ecosystem, int Rank)? Classify(string fileName)
    {
        for (int i = 0; i < NameMarkers.Length; i++)
        {
            if (string.Equals(fileName, NameMarkers[i].Name, StringComparison.Ordinal))
            {
                return (NameMarkers[i].Ecosystem, i);
            }
        }

        string extension = System.IO.Path.GetExtension(fileName);
        for (int i = 0; i < ExtensionMarkers.Length; i++)
        {
            if (string.Equals(extension, ExtensionMarkers[i].Extension, StringComparison.OrdinalIgnoreCase))
            {
                return (ExtensionMarkers[i].Ecosystem, NameMarkers.Length + i);
            }
        }

        return null;
    }

    private static string Directory(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash < 0 ? "." : relativePath[..slash];
    }
}
