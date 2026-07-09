using Networthy.Finance;
using Networthy.Finance.Persistence;

namespace Networthy.Finance.Tests;

/// <summary>
/// The pure net-worth roll-up: per-currency sums where credit balances (stored negative when
/// owed) subtract naturally — assets minus liabilities without a special case.
/// </summary>
public sealed class NetWorthMathTests
{
    private static Account Make(string name, string type, string currency, decimal balance) => new()
    {
        Name = name,
        Type = type,
        CurrencyCode = currency,
        CachedBalance = balance,
    };

    [Fact]
    public void SumsPerCurrency_WithCreditSubtracting()
    {
        var accounts = new[]
        {
            Make("Checking", "checking", "USD", 2500m),
            Make("Savings", "savings", "USD", 10000m),
            Make("Visa", "credit", "USD", -1200m),
            Make("BBVA", "checking", "MXN", 8000m),
        };

        var totals = NetWorthMath.SumByCurrency(accounts);

        Assert.Equal(2, totals.Count);
        Assert.Contains(("MXN", 8000m), totals);
        Assert.Contains(("USD", 11300m), totals);
    }

    [Fact]
    public void CurrencyGrouping_IsCaseInsensitive_AndOutputOrdered()
    {
        var totals = NetWorthMath.SumByCurrency(
        [
            Make("A", "cash", "usd", 10m),
            Make("B", "cash", "USD", 5m),
            Make("C", "cash", "EUR", 1m),
        ]);

        Assert.Equal([("EUR", 1m), ("USD", 15m)], totals);
    }

    [Theory]
    [InlineData("Checking", "checking")]
    [InlineData("credit card", "credit")]
    [InlineData("WALLET", "cash")]
    [InlineData("saving", "savings")]
    [InlineData("brokerage", null)]
    public void NormalizeType_MapsSynonyms_AndRejectsUnknown(string input, string? expected)
    {
        Assert.Equal(expected, Account.NormalizeType(input));
    }

    [Fact]
    public void Visibility_RestrictedAccountIsOnlyVisibleToItsMember()
    {
        var member = Guid.NewGuid();
        var account = Make("Private stash", "cash", "USD", 100m);
        account.RestrictedToUserId = member;

        Assert.True(account.IsVisibleTo(member));
        Assert.False(account.IsVisibleTo(Guid.NewGuid()));
        Assert.False(account.IsVisibleTo(null));

        account.RestrictedToUserId = null;
        Assert.True(account.IsVisibleTo(null));
    }
}
