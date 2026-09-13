using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// Keeps the opt-in real-agent comparison (<c>docs/eval/agent/</c>) frozen.
/// </summary>
/// <remarks>
/// <para>
/// The comparison itself cannot run in CI: it starts a real coding agent, costs
/// real money and needs credentials the offline product deliberately does not
/// have. What CI can guarantee is that the frozen half stays frozen — tasks,
/// acceptance checks, arms, protocol and rubric are fixed <i>before</i> tuning,
/// and a later change to them must be a deliberate new manifest id rather than a
/// quiet edit that makes a result look better.
/// </para>
/// <para>
/// The manifest hash is asserted here for the same reason the retrieval holdout
/// asserts one: a frozen file whose freeze nothing checks is not frozen.
/// </para>
/// </remarks>
public class AgentEvaluationManifestTests
{
    /// <summary>SHA-256 of <c>docs/eval/agent/manifest.json</c> with LF endings.</summary>
    private const string FrozenSha256 =
        "78e392942d504231079506146e259689f6ed060c329362df239620e71d88c1c4";

    private static string DirectoryPath
    {
        get
        {
            for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "RepoContext.slnx")))
                {
                    return Path.Combine(d.FullName, "docs", "eval", "agent");
                }
            }

            throw new InvalidOperationException("Cannot locate the agent evaluation manifest.");
        }
    }

    private static JsonElement Manifest(out string canonicalText)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(DirectoryPath, "manifest.json"));
        // Git may check JSON out with CRLF; the frozen canonical manifest uses LF.
        canonicalText = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return JsonDocument.Parse(canonicalText).RootElement.Clone();
    }

    [Fact]
    public void Manifest_IsFrozen()
    {
        JsonElement manifest = Manifest(out string text);

        Assert.Equal(
            FrozenSha256,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        Assert.Equal("agent-cost-quality-2026-09-12", manifest.GetProperty("id").GetString());
    }

    [Fact]
    public void Manifest_ComparesThreeArmsWithAFixedEnvironment()
    {
        JsonElement manifest = Manifest(out _);

        string[] arms = [.. manifest.GetProperty("arms").EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()!)];
        Assert.Equal<string[]>(["bare", "current", "guarded"], arms);

        string[] fixedAcross = [.. manifest.GetProperty("fixed_across_arms").EnumerateArray()
            .Select(v => v.GetString()!)];
        Assert.Contains(fixedAcross, v => v.Contains("model", StringComparison.Ordinal));
        Assert.Contains(fixedAcross, v => v.Contains("prompt", StringComparison.Ordinal));
        Assert.Contains(fixedAcross, v => v.Contains("commit", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_CoversTheWorkloadTheProductClaimsToHelpWith()
    {
        JsonElement manifest = Manifest(out _);
        JsonElement[] tasks = [.. manifest.GetProperty("tasks").EnumerateArray()];

        Assert.True(tasks.Length >= 10);
        string[] kinds =
            [.. tasks.Select(t => t.GetProperty("kind").GetString()!).Distinct().Order(StringComparer.Ordinal)];
        Assert.Equal<string[]>(["explain", "feature", "fix", "refactor", "review"], kinds);

        string[] languages = [.. tasks.Select(t => t.GetProperty("language").GetString()!)
            .Distinct().Order(StringComparer.Ordinal)];
        Assert.Equal<string[]>(["csharp", "javascript", "typescript"], languages);

        string[] scenarios = [.. tasks
            .SelectMany(t => t.GetProperty("scenario").EnumerateArray())
            .Select(s => s.GetString()!)
            .Distinct()];
        Assert.Contains("long_file", scenarios);
        Assert.Contains("cross_file", scenarios);
        Assert.Contains("stale_index", scenarios);
        Assert.Contains("compaction", scenarios);

        // An untouched holdout is what keeps the release decision honest.
        Assert.Contains(tasks, t => t.GetProperty("holdout").GetBoolean());
    }

    [Fact]
    public void Manifest_KeepsFailuresInTheCostAndForbidsUnmeasuredClaims()
    {
        JsonElement manifest = Manifest(out _);

        string[] rules = [.. manifest.GetProperty("accounting_rules").EnumerateArray()
            .Select(v => v.GetString()!)];
        Assert.Contains(rules, r => r.Contains("undefined, never zero", StringComparison.Ordinal));
        Assert.Contains(rules, r => r.Contains("disjoint", StringComparison.Ordinal));
        Assert.Contains(rules, r => r.Contains("never proves a saving", StringComparison.Ordinal));

        string retention = manifest.GetProperty("protocol").GetProperty("retention").GetString()!;
        Assert.Contains("denominator", retention, StringComparison.Ordinal);

        JsonElement promotion = manifest.GetProperty("promotion_criteria");
        Assert.Contains(
            "not an achieved result",
            promotion.GetProperty("cost").GetString()!,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not establish equal quality",
            promotion.GetProperty("uncertainty").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_ReferencesTheFrozenSourceArchives()
    {
        JsonElement manifest = Manifest(out _);
        string[] taskRepositories = [.. manifest.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("repository").GetString()!)];

        foreach (JsonElement repository in manifest.GetProperty("repositories").EnumerateArray())
        {
            string archive = Path.Combine(
                DirectoryPath,
                repository.GetProperty("archive").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(archive), $"missing archive {archive}");
            Assert.Equal(
                repository.GetProperty("archive_sha256").GetString(),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))));
        }

        string[] declared = [.. manifest.GetProperty("repositories").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()!)];
        Assert.All(taskRepositories, id => Assert.Contains(id, declared));
    }

    [Fact]
    public void Rubric_DefinesSeverityAndTheLimitsOfItsOwnEvidence()
    {
        string rubric = File.ReadAllText(Path.Combine(DirectoryPath, "rubric.md"));

        Assert.Contains("`severe`", rubric, StringComparison.Ordinal);
        Assert.Contains("human_repair_minutes", rubric, StringComparison.Ordinal);
        Assert.Contains(
            "cannot establish equal quality", rubric, StringComparison.Ordinal);
    }
}
