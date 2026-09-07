using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>Frozen real source snapshots and source-inspected evidence labels.</summary>
public sealed record HoldoutManifest(
    string Id, IReadOnlyList<HoldoutSource> Repositories, IReadOnlyList<HoldoutTask> Tasks)
{
    public const string FrozenSha256 = "bfc0a7174b0a31da48ff5709e0e120a6014289b7714061db6e3209a7c3b45fa9";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        NewLine = "\n",
    };

    public static string DirectoryPath
    {
        get
        {
            for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "RepoContext.slnx")))
                    return Path.Combine(d.FullName, "docs", "eval", "holdout");
            }

            throw new InvalidOperationException("Cannot locate the holdout manifest.");
        }
    }

    public static HoldoutManifest Load()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(DirectoryPath, "manifest.json"));
        // Git may check JSON out with CRLF; the frozen canonical manifest uses LF.
        string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(FrozenSha256, Hash(Encoding.UTF8.GetBytes(text)));
        return JsonSerializer.Deserialize<HoldoutManifest>(text, JsonOptions)!;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Validate()
    {
        Assert.Equal(36, Tasks.Count);
        Assert.Equal(6, Tasks.Count(t => t.HistoricalDiagnostic));
        Assert.Equal(30, Tasks.Count(t => !t.HistoricalDiagnostic));
        Assert.Equal(Tasks.Count, Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, Tasks.Select(t => t.Language).Distinct(StringComparer.Ordinal).Count());
        foreach (HoldoutSource source in Repositories)
        {
            byte[] archive = File.ReadAllBytes(Path.Combine(DirectoryPath, source.Archive));
            Assert.Equal(source.ArchiveSha256, Hash(archive));
            using var zip = new ZipArchive(new MemoryStream(archive));
            Assert.Equal(source.Files.Select(f => f.Path), zip.Entries
                .Where(e => !e.FullName.EndsWith('/'))
                .Select(e => e.FullName).Order(StringComparer.Ordinal));
            Assert.NotNull(zip.GetEntry(source.LicensePath));
            foreach (HoldoutFile file in source.Files)
            {
                Assert.False(Path.IsPathRooted(file.Path));
                Assert.DoesNotContain("..", file.Path.Split('/'));
                Assert.DoesNotContain(':', file.Path);
                using Stream stream = zip.GetEntry(file.Path)!.Open();
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                Assert.Equal(file.Sha256, Hash(bytes.ToArray()));
            }

            foreach (HoldoutTask task in Tasks.Where(t => t.Repository == source.Id))
            {
                Assert.NotEmpty(task.RequiredSpans);
                Assert.False(string.IsNullOrWhiteSpace(task.Rationale));
                foreach (HoldoutSpan span in task.RequiredSpans)
                {
                    using var reader = new StreamReader(zip.GetEntry(span.Path)!.Open());
                    string[] lines = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal)
                        .TrimEnd('\n').Split('\n');
                    Assert.InRange(span.StartLine, 1, lines.Length);
                    Assert.InRange(span.EndLine, span.StartLine, lines.Length);
                    string evidence = string.Join('\n', lines[(span.StartLine - 1)..span.EndLine]);
                    Assert.Equal(span.Sha256, Hash(Encoding.UTF8.GetBytes(evidence)));
                    Assert.Equal("production", source.Files.Single(f => f.Path == span.Path).Role);
                }

                foreach (HoldoutRelationship relation in task.RequiredRelationships)
                {
                    Assert.Contains(relation.From, task.RequiredPaths);
                    Assert.Contains(relation.To, task.RequiredPaths);
                }
            }
        }
    }
}

public sealed record HoldoutSource(
    string Id, string Upstream, string Commit, string Archive, string ArchiveSha256,
    string LicensePath, IReadOnlyList<HoldoutFile> Files);

public sealed record HoldoutFile(string Path, string Role, string Sha256);
public sealed record HoldoutSpan(string Path, int StartLine, int EndLine, string Sha256);
public sealed record HoldoutRelationship(string From, string To, string Kind);
public sealed record HoldoutTask(
    string Id, string Repository, string Query, string Language, string Class,
    bool HistoricalDiagnostic, string Rationale, IReadOnlyList<HoldoutSpan> RequiredSpans,
    IReadOnlyList<HoldoutRelationship> RequiredRelationships)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> RequiredPaths =>
        RequiredSpans.Select(s => s.Path).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>Each archive is indexed independently with current default configuration.</summary>
public sealed class HoldoutRepo : IDisposable
{
    public HoldoutRepo(HoldoutSource source)
    {
        Root = Path.Combine(Path.GetTempPath(), "repoctx-holdout", Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(Path.Combine(HoldoutManifest.DirectoryPath, source.Archive), Root);
        RepoLayout layout = RepoLayout.For(Root);
        Directory.CreateDirectory(layout.IndexDirectory);
        Config = RepoctxConfig.CreateDefault();
        new Indexer(layout, Config, "holdout").Run(full: true);
        Store = IndexStore.Open(layout.DatabasePath);
    }

    public string Root { get; }
    public RepoctxConfig Config { get; }
    public IndexStore Store { get; }

    public void Dispose()
    {
        Store.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
