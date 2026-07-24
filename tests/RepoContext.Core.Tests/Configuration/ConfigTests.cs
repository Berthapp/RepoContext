using RepoContext.Core.Configuration;
using RepoContext.Core.Query;

namespace RepoContext.Core.Tests.Configuration;

public class ConfigTests
{
    [Fact]
    public void Default_RoundTrips()
    {
        RepoctxConfig original = RepoctxConfig.CreateDefault();
        string json = ConfigStore.Serialize(original);
        RepoctxConfig loaded = ConfigStore.Deserialize(json);

        Assert.Equal(original.Include, loaded.Include);
        Assert.Equal(original.Exclude, loaded.Exclude);
        Assert.Equal(original.RespectGitignore, loaded.RespectGitignore);
        Assert.Equal(original.SensitiveFiles, loaded.SensitiveFiles);
        Assert.Equal(512, loaded.Indexing.MaxFileSizeKb);
        Assert.Equal(0.4, loaded.Ranking.Weights.Fts);
    }

    [Fact]
    public void Default_UsesCamelCaseKeys()
    {
        string json = ConfigStore.Serialize(RepoctxConfig.CreateDefault());
        Assert.Contains("\"respectGitignore\"", json);
        Assert.Contains("\"maxFileSizeKb\"", json);
    }

    [Fact]
    public void Hash_IsStableAndSensitive()
    {
        string h1 = ConfigStore.ComputeHash(RepoctxConfig.CreateDefault());
        string h2 = ConfigStore.ComputeHash(RepoctxConfig.CreateDefault());
        Assert.Equal(h1, h2);

        RepoctxConfig changed = RepoctxConfig.CreateDefault() with { RespectGitignore = false };
        Assert.NotEqual(h1, ConfigStore.ComputeHash(changed));
    }

    [Fact]
    public void Hash_SortsSynonymMaps_AndSeparatesLiveFromIndexingConfig()
    {
        RepoctxConfig baseline = RepoctxConfig.CreateDefault();
        RepoctxConfig first = baseline with
        {
            Ranking = baseline.Ranking with
            {
                Synonyms = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["auth"] = ["login"],
                    ["cache"] = ["memo"],
                },
            },
        };
        RepoctxConfig reverseInsertion = first with
        {
            Ranking = first.Ranking with
            {
                Synonyms = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["cache"] = ["memo"],
                    ["auth"] = ["login"],
                },
            },
        };

        Assert.Equal(ConfigStore.ComputeHash(first), ConfigStore.ComputeHash(reverseInsertion));
        Assert.Equal(ConfigStore.ComputeIndexHash(baseline), ConfigStore.ComputeIndexHash(first));
        Assert.NotEqual(ConfigStore.ComputeHash(baseline), ConfigStore.ComputeHash(first));
    }

    [Theory]
    [InlineData("change the Login logic", new[] { "change", "the", "login", "logic" })]
    [InlineData("loginUser()", new[] { "loginuser" })]
    [InlineData("   ", new string[0])]
    public void FtsQuery_Tokenizes(string input, string[] expected)
    {
        Assert.Equal(expected, FtsQuery.Tokenize(input));
    }

    [Fact]
    public void FtsQuery_Build_QuotesTermsAndOrsThem()
    {
        Assert.Equal("\"login\" OR \"logic\"", FtsQuery.Build("login logic"));
        Assert.Null(FtsQuery.Build("!!!"));
    }
}
