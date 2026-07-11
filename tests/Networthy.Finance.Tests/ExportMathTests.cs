using Networthy.Finance;

namespace Networthy.Finance.Tests;

/// <summary>
/// The CSV builder is pure so the escaping rules — the part spreadsheet imports live or die
/// on — are pinned without a database. RFC-4180: quote when a field contains a comma, quote,
/// or newline; double embedded quotes; touch nothing else.
/// </summary>
public sealed class ExportMathTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("has space", "has space")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("carriage\rreturn", "\"carriage\rreturn\"")]
    [InlineData("\"", "\"\"\"\"")]
    public void EscapeCsvField_QuotesExactlyWhatNeedsQuoting(string field, string expected) =>
        Assert.Equal(expected, ExportMath.EscapeCsvField(field));

    [Fact]
    public void BuildCsv_EmitsHeaderThenRows_EveryFieldEscaped()
    {
        var csv = ExportMath.BuildCsv(
            ["a", "b"],
            [
                (string[])["1", "x,y"],
                (string[])["2", "plain"],
            ]);

        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(["a,b", "1,\"x,y\"", "2,plain"], lines);
    }

    [Fact]
    public void BuildTransactionsCsv_UsesInvariantDatesAndAmounts()
    {
        var csv = ExportMath.BuildTransactionsCsv(
        [
            new ExportMath.TransactionRow(
                new DateOnly(2026, 7, 9), "Chase Checking", "Dining", "expense",
                1234.5m, "USD", "Tacos, al pastor"),
            new ExportMath.TransactionRow(
                new DateOnly(2026, 7, 10), "Cash", "", "income", 6m, "USD", "found a bill"),
        ]);

        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.Equal("date,account,category,direction,amount,currency,description", lines[0]);
        // The comma-bearing description is quoted; the amount is invariant "0.00".
        Assert.Equal("2026-07-09,Chase Checking,Dining,expense,1234.50,USD,\"Tacos, al pastor\"", lines[1]);
        // An uncategorized row exports an empty category field, not a placeholder.
        Assert.Equal("2026-07-10,Cash,,income,6.00,USD,found a bill", lines[2]);
    }

    [Fact]
    public void BuildTransactionsCsv_RoundTripsAnEvilDescription()
    {
        var evil = "he said \"buy, now\"\nthen left";
        var csv = ExportMath.BuildTransactionsCsv(
            [new ExportMath.TransactionRow(new DateOnly(2026, 1, 1), "A", "C", "expense", 1m, "USD", evil)]);

        Assert.Contains("\"he said \"\"buy, now\"\"\nthen left\"", csv);
    }

    [Fact]
    public void Timestamp_IsUtcAndInvariant()
    {
        var at = new DateTimeOffset(2026, 7, 9, 18, 30, 0, TimeSpan.FromHours(-6));
        Assert.Equal("2026-07-10 00:30:00Z", ExportMath.Timestamp(at));
    }
}
