using Networthy.Finance;
using Networthy.Finance.Persistence;

namespace Networthy.Finance.Tests;

/// <summary>
/// The "can I afford X" verdict: liquid excludes credit (a credit line is debt capacity, not
/// affordability), and the thresholds are honest — a hard no past liquid, a flagged yes when
/// the amount is a real share of it.
/// </summary>
public sealed class AffordabilityMathTests
{
    private static Account Make(string type, decimal balance) => new()
    {
        Name = type,
        Type = type,
        CurrencyCode = "USD",
        CachedBalance = balance,
    };

    [Fact]
    public void LiquidBalance_ExcludesCredit()
    {
        var liquid = AffordabilityMath.LiquidBalance(
        [
            Make("checking", 2000m),
            Make("savings", 5000m),
            Make("cash", 100m),
            Make("credit", -1500m), // owed — must not reduce (or inflate) liquidity
        ]);

        Assert.Equal(7100m, liquid);
    }

    [Theory]
    [InlineData(100, 7100, Affordability.Comfortably)]  // under 10%
    [InlineData(710, 7100, Affordability.Comfortably)]  // exactly 10%
    [InlineData(711, 7100, Affordability.Yes)]          // just past 10%
    [InlineData(7100, 7100, Affordability.Yes)]         // all of it — possible, flagged
    [InlineData(7101, 7100, Affordability.No)]          // beyond liquid
    [InlineData(50, 0, Affordability.No)]               // nothing liquid
    public void Verdict_Thresholds(double amount, double liquid, Affordability expected)
    {
        Assert.Equal(expected, AffordabilityMath.Verdict((decimal)amount, (decimal)liquid));
    }

    [Fact]
    public void ShareOfLiquid_ClampsAtOne_AndHandlesZero()
    {
        Assert.Equal(0.5m, AffordabilityMath.ShareOfLiquid(50m, 100m));
        Assert.Equal(1m, AffordabilityMath.ShareOfLiquid(200m, 100m));
        Assert.Equal(1m, AffordabilityMath.ShareOfLiquid(1m, 0m));
    }
}
