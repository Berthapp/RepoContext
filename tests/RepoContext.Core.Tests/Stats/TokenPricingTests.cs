using RepoContext.Core.Configuration;
using RepoContext.Core.Stats;

namespace RepoContext.Core.Tests.Stats;

/// <summary>The optional money view over token savings (ADR 0012).</summary>
public class TokenPricingTests
{
    [Fact]
    public void DefaultConfig_HasNoRate_SoFormattingIsNull()
    {
        TokenPricing pricing = TokenPricing.From(RepoctxConfig.CreateDefault());

        Assert.False(pricing.Enabled);
        Assert.Null(pricing.Format(1_000_000));
    }

    [Fact]
    public void PositiveSaving_FormatsAsUsd()
    {
        var pricing = new TokenPricing(5.0, "USD");

        Assert.True(pricing.Enabled);
        Assert.Equal("$5.00", pricing.Format(1_000_000));
        Assert.Equal("$0.37", pricing.Format(73_358));
    }

    [Fact]
    public void NegativeSaving_KeepsSign()
    {
        var pricing = new TokenPricing(5.0, "USD");

        Assert.Equal("-$0.50", pricing.Format(-100_000));
    }

    [Fact]
    public void NearZero_DoesNotPrintStraySign()
    {
        var pricing = new TokenPricing(5.0, "USD");

        Assert.Equal("$0.00", pricing.Format(-79));
    }

    [Fact]
    public void NonUsdCurrency_UsesCodePrefix()
    {
        var pricing = new TokenPricing(4.0, "EUR");

        Assert.Equal("EUR 4.00", pricing.Format(1_000_000));
    }

    [Fact]
    public void ZeroOrNegativeRate_IsDisabled()
    {
        Assert.False(new TokenPricing(0, "USD").Enabled);
        Assert.Null(new TokenPricing(-1, "USD").Format(1_000_000));
    }
}
