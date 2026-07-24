using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;

namespace RepoContext.Core.Tests.Indexing;

/// <summary>Query-time token calibration (ADR 0012).</summary>
public class TokenScaleTests
{
    [Fact]
    public void DefaultConfig_IsIdentity()
    {
        TokenScale scale = TokenScale.From(RepoctxConfig.CreateDefault());

        Assert.True(scale.IsIdentity);
        Assert.Null(scale.Label);
        Assert.Equal(1234, scale.Apply(1234));
    }

    [Fact]
    public void DefaultStruct_IsIdentity()
    {
        TokenScale scale = default;

        Assert.True(scale.IsIdentity);
        Assert.Equal(77, scale.Apply(77));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("Claude")]
    public void ClaudeProfile_ScalesUp_AndCarriesLabel(string profile)
    {
        var config = RepoctxConfig.CreateDefault() with
        {
            Tokens = new TokenOptions { Profile = profile },
        };

        TokenScale scale = TokenScale.From(config);

        Assert.False(scale.IsIdentity);
        Assert.Equal("claude", scale.Label);
        Assert.Equal(120, scale.Apply(100));
        // Rounds up: budgets must never undershoot.
        Assert.Equal(122, scale.Apply(101));
    }

    [Fact]
    public void ExplicitFactor_OverridesProfile()
    {
        var config = RepoctxConfig.CreateDefault() with
        {
            Tokens = new TokenOptions { Profile = "claude", Factor = 1.5 },
        };

        TokenScale scale = TokenScale.From(config);

        Assert.Equal("x1.5", scale.Label);
        Assert.Equal(150, scale.Apply(100));
    }

    [Fact]
    public void UnknownProfile_FallsBackToRawCounts()
    {
        var config = RepoctxConfig.CreateDefault() with
        {
            Tokens = new TokenOptions { Profile = "gemini-9000" },
        };

        TokenScale scale = TokenScale.From(config);

        Assert.True(scale.IsIdentity);
        Assert.Null(scale.Label);
    }

    [Fact]
    public void FactorOfOne_IsIdentityWithoutLabel()
    {
        var config = RepoctxConfig.CreateDefault() with
        {
            Tokens = new TokenOptions { Factor = 1.0 },
        };

        Assert.True(TokenScale.From(config).IsIdentity);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(101.0)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidOrExtremeFactor_FallsBackToIdentity(double factor)
    {
        var config = RepoctxConfig.CreateDefault() with
        {
            Tokens = new TokenOptions { Factor = factor },
        };

        Assert.True(TokenScale.From(config).IsIdentity);
    }

    [Fact]
    public void Apply_SaturatesInsteadOfOverflowing()
    {
        var config = RepoctxConfig.CreateDefault() with
        {
            Tokens = new TokenOptions { Factor = 100.0 },
        };

        Assert.Equal(int.MaxValue, TokenScale.From(config).Apply(int.MaxValue));
    }
}
