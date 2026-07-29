namespace Networthy.Finance.Tests;

/// <summary>
/// The rule that keeps "I uploaded the PDF, then the bank's CSV for the same month" from doubling
/// the ledger — and, just as importantly, keeps it from swallowing a household's genuine second
/// identical purchase on the same day.
/// </summary>
public sealed class StatementDeduplicationTests
{
    private static ExtractedLine Line(string date, string description, decimal amount, string direction = "expense") =>
        new(DateOnly.Parse(date), description, amount, direction, null);

    private static string Key(string date, string description, decimal amount, string direction = "expense") =>
        StatementDeduplication.ContentKey(DateOnly.Parse(date), amount, direction, description);

    [Fact]
    public void SameMovement_SpelledDifferently_SharesAKey()
    {
        // The PDF's text extraction pads columns; the CSV export doesn't. Same purchase.
        Assert.Equal(
            Key("2026-06-03", "TIENDA  EJEMPLO   4411", 92.50m),
            Key("2026-06-03", "tienda ejemplo 4411", 92.50m));
    }

    [Theory]
    [InlineData("2026-06-04", "TIENDA EJEMPLO 4411", 92.50, "expense")] // different day
    [InlineData("2026-06-03", "TIENDA EJEMPLO 4412", 92.50, "expense")] // different merchant reference
    [InlineData("2026-06-03", "TIENDA EJEMPLO 4411", 92.51, "expense")] // a cent apart
    [InlineData("2026-06-03", "TIENDA EJEMPLO 4411", 92.50, "income")]  // the other direction
    public void DifferentMovements_DoNotShareAKey(string date, string description, decimal amount, string direction)
    {
        Assert.NotEqual(
            Key("2026-06-03", "TIENDA EJEMPLO 4411", 92.50m),
            Key(date, description, amount, direction));
    }

    [Fact]
    public void ReimportingAPeriod_PostsNothingNew()
    {
        var lines = new[]
        {
            Line("2026-06-01", "RENT", 12000m),
            Line("2026-06-03", "TIENDA EJEMPLO 4411", 92.50m),
            Line("2026-06-15", "PAYROLL", 24000m, "income"),
        };

        var (toPost, already) = StatementDeduplication.Partition(lines, lines.Select(StatementDeduplication.ContentKey));

        Assert.Empty(toPost);
        Assert.Equal(3, already.Count);
    }

    [Fact]
    public void OverlappingPeriod_PostsOnlyTheNewLines()
    {
        var posted = new[] { Line("2026-06-01", "RENT", 12000m), Line("2026-06-03", "TIENDA EJEMPLO 4411", 92.50m) };
        var second = new[]
        {
            Line("2026-06-01", "RENT", 12000m),                  // already there
            Line("2026-06-03", "TIENDA EJEMPLO 4411", 92.50m),      // already there
            Line("2026-06-20", "PHARMACY", 310.00m),             // new
        };

        var (toPost, already) = StatementDeduplication.Partition(second, posted.Select(StatementDeduplication.ContentKey));

        Assert.Equal(2, already.Count);
        Assert.Equal("PHARMACY", Assert.Single(toPost).Description);
    }

    [Fact]
    public void TwoIdenticalChargesInOneDay_BothSurviveWhenOnlyOneIsPosted()
    {
        // Two coffees, same price, same day, same merchant — a real thing households do. The
        // account already holds ONE of them, so exactly one more must post.
        var posted = new[] { Line("2026-06-03", "CAFE", 4.50m) };
        var statement = new[] { Line("2026-06-03", "CAFE", 4.50m), Line("2026-06-03", "CAFE", 4.50m) };

        var (toPost, already) = StatementDeduplication.Partition(statement, posted.Select(StatementDeduplication.ContentKey));

        Assert.Single(toPost);
        Assert.Single(already);
    }

    [Fact]
    public void NothingPostedYet_EverythingIsNew()
    {
        var lines = new[] { Line("2026-06-01", "RENT", 12000m), Line("2026-06-03", "CAFE", 4.50m) };

        var (toPost, already) = StatementDeduplication.Partition(lines, []);

        Assert.Equal(2, toPost.Count);
        Assert.Empty(already);
    }

    [Fact]
    public void PartitionPreservesStatementOrder()
    {
        var posted = new[] { Line("2026-06-02", "B", 2m) };
        var statement = new[] { Line("2026-06-01", "A", 1m), Line("2026-06-02", "B", 2m), Line("2026-06-03", "C", 3m) };

        var (toPost, _) = StatementDeduplication.Partition(statement, posted.Select(StatementDeduplication.ContentKey));

        Assert.Equal(["A", "C"], toPost.Select(l => l.Description));
    }
}
