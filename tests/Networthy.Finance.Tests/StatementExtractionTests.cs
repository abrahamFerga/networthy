using Networthy.Finance;

namespace Networthy.Finance.Tests;

/// <summary>
/// The template leg of ADR-0004's hybrid extraction: real bank-export shapes parse
/// deterministically — CSV with signed amounts, CSV with debit/credit columns, quoted fields,
/// accounting-style negatives, and OFX/QFX STMTTRN blocks — and the keyword category suggester
/// only ever suggests categories the household actually has.
/// </summary>
public sealed class StatementExtractionTests
{
    private static readonly IReadOnlyList<string> Categories =
        ["Groceries", "Dining", "Transportation", "Salary", "Subscriptions"];

    [Fact]
    public void Csv_SignedAmounts_ParseWithDirections()
    {
        const string csv = """
            Date,Description,Amount
            2026-07-01,WHOLE FOODS MARKET,-82.45
            2026-07-02,"ACME PAYROLL, INC — DIRECT DEP",2500.00
            07/03/2026,BLUE BOTTLE COFFEE,-6.50
            """;

        var lines = StatementExtraction.TryExtractCsv(csv, Categories);

        Assert.NotNull(lines);
        Assert.Equal(3, lines!.Count);
        Assert.Equal(("expense", 82.45m, "Groceries"), (lines[0].Direction, lines[0].Amount, lines[0].SuggestedCategory));
        Assert.Equal(("income", 2500m, "Salary"), (lines[1].Direction, lines[1].Amount, lines[1].SuggestedCategory));
        Assert.Equal(new DateOnly(2026, 7, 3), lines[2].Date);
        Assert.Equal("Dining", lines[2].SuggestedCategory); // "coffee" keyword
    }

    [Fact]
    public void Csv_DebitCreditColumns_AndAccountingNegatives()
    {
        const string csv = """
            Date,Payee,Debit,Credit
            2026-07-05,UBER TRIP,(23.10),
            2026-07-06,INTEREST PAYMENT,,1.25
            """;

        var lines = StatementExtraction.TryExtractCsv(csv, Categories);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal(("expense", 23.10m, "Transportation"), (lines[0].Direction, lines[0].Amount, lines[0].SuggestedCategory));
        Assert.Equal(("income", 1.25m), (lines[1].Direction, lines[1].Amount));
    }

    [Fact]
    public void Csv_WithoutARecognizableHeader_ReturnsNull()
    {
        Assert.Null(StatementExtraction.TryExtractCsv("just,some,cells\n1,2,3", Categories));
        Assert.Null(StatementExtraction.TryExtractCsv("a single line", Categories));
    }

    [Fact]
    public void Ofx_StmtTrnBlocks_Parse()
    {
        const string ofx = """
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN>
            <TRNTYPE>DEBIT
            <DTPOSTED>20260701120000
            <TRNAMT>-45.00
            <NAME>NETFLIX.COM
            </STMTTRN>
            <STMTTRN>
            <TRNTYPE>CREDIT
            <DTPOSTED>20260702
            <TRNAMT>1200.00
            <MEMO>PAYROLL ACME
            </STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;

        var lines = StatementExtraction.TryExtractOfx(ofx, Categories);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal(("expense", 45m, "Subscriptions"), (lines[0].Direction, lines[0].Amount, lines[0].SuggestedCategory));
        Assert.Equal((new DateOnly(2026, 7, 2), "income", "Salary"), (lines[1].Date, lines[1].Direction, lines[1].SuggestedCategory));
    }

    [Fact]
    public void Ofx_WithoutStmtTrn_ReturnsNull()
    {
        Assert.Null(StatementExtraction.TryExtractOfx("<OFX><SIGNONMSGSRSV1/></OFX>", Categories));
    }

    [Fact]
    public void SuggestCategory_OnlySuggestsCategoriesTheHouseholdHas()
    {
        // "netflix" maps to Subscriptions — but this household deleted that category.
        Assert.Null(StatementExtraction.SuggestCategory("NETFLIX.COM", ["Groceries"]));
        // Direct category-name containment always wins.
        Assert.Equal("Groceries", StatementExtraction.SuggestCategory("GROCERIES R US", ["Groceries"]));
    }

    [Fact]
    public void SplitCsvLine_HonorsQuotedFieldsAndDoubledQuotes()
    {
        var fields = StatementExtraction.SplitCsvLine("a,\"b, with comma\",\"he said \"\"hi\"\"\",d");
        Assert.Equal(["a", "b, with comma", "he said \"hi\"", "d"], fields);
    }
}
