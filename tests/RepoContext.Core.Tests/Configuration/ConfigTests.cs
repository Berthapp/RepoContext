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
        Assert.Equal("o200k", loaded.Tokens.Profile);
        Assert.Null(loaded.Tokens.Factor);
        Assert.Null(loaded.Pricing.InputPerMtok);
    }

    [Fact]
    public void ConfigWithoutTokenOrPricingKeys_DeserializesToDefaults()
    {
        // Configs written before M8 have neither key; they must load, not fail.
        const string legacy = "{\"include\":[\"src\"],\"exclude\":[]}";
        RepoctxConfig loaded = ConfigStore.Deserialize(legacy);

        Assert.Equal("o200k", loaded.Tokens.Profile);
        Assert.Null(loaded.Pricing.InputPerMtok);
        Assert.Equal("USD", loaded.Pricing.Currency);
    }

    [Fact]
    public void TokensAndPricing_RoundTrip()
    {
        RepoctxConfig original = RepoctxConfig.CreateDefault() with
        {
            Tokens = new TokenOptions { Profile = "claude", Factor = 1.25 },
            Pricing = new PricingOptions { InputPerMtok = 5.0, Currency = "EUR" },
        };

        RepoctxConfig loaded = ConfigStore.Deserialize(ConfigStore.Serialize(original));

        Assert.Equal("claude", loaded.Tokens.Profile);
        Assert.Equal(1.25, loaded.Tokens.Factor);
        Assert.Equal(5.0, loaded.Pricing.InputPerMtok);
        Assert.Equal("EUR", loaded.Pricing.Currency);
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
