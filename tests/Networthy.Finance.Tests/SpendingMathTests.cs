using Networthy.Finance;
using Networthy.Finance.Persistence;

namespace Networthy.Finance.Tests;

/// <summary>
/// Transaction pure logic: direction normalization, the balance delta each direction applies,
/// and the per-category spending summary behind "how much did we spend on X".
/// </summary>
public sealed class SpendingMathTests
{
    private static Transaction Make(decimal amount, string direction, Guid? categoryId = null, string currency = "USD") => new()
    {
        Amount = amount,
        Direction = direction,
        CategoryId = categoryId,
        CurrencyCode = currency,
        Description = "test",
        Source = "manual",
    };

    [Theory]
    [InlineData("expense", "expense")]
    [InlineData("Spending", "expense")]
    [InlineData("DEBIT", "expense")]
    [InlineData("income", "income")]
    [InlineData("deposit", "income")]
    [InlineData("transfer", null)]
    public void NormalizeDirection_MapsSynonyms_AndRejectsUnknown(string input, string? expected)
    {
        Assert.Equal(expected, Transaction.NormalizeDirection(input));
    }

    [Fact]
    public void BalanceDelta_IncomeAdds_ExpenseSubtracts()
    {
        Assert.Equal(100m, Make(100m, "income").BalanceDelta);
        Assert.Equal(-42.50m, Make(42.50m, "expense").BalanceDelta);
    }

    [Fact]
    public void SummarizeByCategory_GroupsSumsAndOrdersLargestFirst()
    {
        var groceries = Guid.NewGuid();
        var dining = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [groceries] = "Groceries", [dining] = "Dining" };

        var summary = SpendingMath.SummarizeByCategory(
        [
            Make(120m, "expense", groceries),
            Make(80m, "expense", groceries),
            Make(60m, "expense", dining),
            Make(15m, "expense", null),
        ], names);

        Assert.Equal(3, summary.Count);
        Assert.Equal(("Groceries", 200m, 2), (summary[0].Category, summary[0].Total, summary[0].Count));
        Assert.Equal("Dining", summary[1].Category);
        Assert.Equal("(uncategorized)", summary[2].Category);
    }

    [Fact]
    public void SummarizeByCategory_KeepsCurrenciesSeparate()
    {
        var summary = SpendingMath.SummarizeByCategory(
            [Make(100m, "expense", null, "USD"), Make(100m, "expense", null, "MXN")],
            new Dictionary<Guid, string>());

        Assert.Equal(2, summary.Count);
        Assert.All(summary, l => Assert.Equal(100m, l.Total));
        Assert.NotEqual(summary[0].Currency, summary[1].Currency);
    }
}
