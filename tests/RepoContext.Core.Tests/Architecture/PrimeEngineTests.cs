using RepoContext.Core.Architecture;

namespace RepoContext.Core.Tests.Architecture;

/// <summary>Quantization behind the cache-stable primer (ADR 0012).</summary>
public class PrimeEngineTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(75, 75)]      // below 100: exact
    [InlineData(99, 99)]
    [InlineData(123, 120)]    // two significant digits
    [InlineData(150, 150)]
    [InlineData(9_934, 9_900)]
    [InlineData(9_950, 10_000)]
    [InlineData(999_499, 1_000_000)]
    public void Quantize_KeepsTwoSignificantDigits(int value, int expected) =>
        Assert.Equal(expected, PrimeEngine.Quantize(value));

    [Fact]
    public void Quantize_AbsorbsSmallEdits()
    {
        // The property the primer's cache stability rests on: an ordinary
        // edit does not move the quantized aggregate.
        Assert.Equal(PrimeEngine.Quantize(9_934), PrimeEngine.Quantize(9_939));
        Assert.Equal(PrimeEngine.Quantize(11_800), PrimeEngine.Quantize(12_240));
    }
}
