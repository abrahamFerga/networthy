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
    public void Text_PdfStyleLines_ParseWithDatesAmountsAndDirections()
    {
        const string text = """
            FIRST NATIONAL BANK  Statement Period 06/01/2026 - 06/30/2026
            Beginning balance                                  4,200.00
            2026-06-03  WHOLE FOODS MARKET #123                  -82.45
            2026-06-05  ACME PAYROLL DIRECT DEP               2,500.00
            06/10/2026  UBER TRIP HELP.UBER.COM                 (23.10)
            2026-06-15  NETFLIX.COM                              -15.49
            Ending balance                                     6,578.96
            Page 1 of 2
            """;

        var lines = StatementExtraction.TryExtractText(text, Categories);

        Assert.NotNull(lines);
        Assert.Equal(4, lines!.Count); // balance/page noise skipped
        Assert.Equal(("expense", 82.45m, "Groceries"), (lines[0].Direction, lines[0].Amount, lines[0].SuggestedCategory));
        Assert.Equal(("income", 2500m, "Salary"), (lines[1].Direction, lines[1].Amount, lines[1].SuggestedCategory));
        Assert.Equal(("expense", 23.10m, new DateOnly(2026, 6, 10)), (lines[2].Direction, lines[2].Amount, lines[2].Date));
        Assert.Equal("Subscriptions", lines[3].SuggestedCategory);
    }

    [Fact]
    public void Text_WithoutTransactionLines_ReturnsNull()
    {
        Assert.Null(StatementExtraction.TryExtractText("Dear customer, thank you for banking with us.", Categories));
    }

    [Fact]
    public void SplitCsvLine_HonorsQuotedFieldsAndDoubledQuotes()
    {
        var fields = StatementExtraction.SplitCsvLine("a,\"b, with comma\",\"he said \"\"hi\"\"\",d");
        Assert.Equal(["a", "b, with comma", "he said \"hi\"", "d"], fields);
    }

    [Fact]
    public void Text_ColumnarOcrBlocks_AreZippedBackIntoRows()
    {
        // Verbatim Tesseract output (via Tika) for a scanned tabular statement: the table came
        // back as COLUMNS — every date, then every description, then every amount, with OCR
        // noise in two amounts. The row-wise parser sees nothing; the columnar fallback must.
        const string ocr = """
            NORTHWIND SAVINGS BANK - STATEMENT
            Account ending in 4321

            2026-07-11

            2026-07-12

            2026-07-14

            2026-07-15

            BOOKSTORE

            FARMERS MARKET

            FREELANCE INCOME

            WATER UTILITY

            -19:°.99

            -34.50

            +800 .00

            -60.75
            """;

        var lines = StatementExtraction.TryExtractText(ocr, Categories);

        Assert.NotNull(lines);
        Assert.Equal(4, lines!.Count);
        Assert.Equal((new DateOnly(2026, 7, 11), "BOOKSTORE", 19.99m, "expense"),
            (lines[0].Date, lines[0].Description, lines[0].Amount, lines[0].Direction));
        Assert.Equal(("FREELANCE INCOME", 800m, "income"),
            (lines[2].Description, lines[2].Amount, lines[2].Direction));
        Assert.Equal(("WATER UTILITY", 60.75m, "expense"),
            (lines[3].Description, lines[3].Amount, lines[3].Direction));
    }

    [Fact]
    public void Text_CurrencyTaggedMidlineAmounts_ParseWithSignBasedDirections()
    {
        // Payoneer-style layout (the real-world shape that motivated this): "dd MMM, yyyy"
        // dates, the amount MID-LINE tagged with a currency code, and every line ending in
        // payout-method + running-balance columns that must not be mistaken for the amount —
        // note the last column has no decimals, so the old end-anchored pattern saw nothing.
        const string text = """
            Account Statement
            Casandra Ejemplo Perez Period 03/26/2025 - 03/26/2026
            CALLE FICTICIA 1234 COL CENTRO Issuing Date 03/26/2026
            Ciudad Ejemplo, Mexico, 00000
            98765432101
            Date Description Amount Currency Payout Method Running Balance
            03 Mar, 2026 Reward from Payview 250.00 USD USD balance 250
            02 Mar, 2026 Withdrawal to Banco Ejemplo (0976) -6392.00 USD USD balance 0
            27 Feb, 2026 Payment from Acme Corporation 6392.00 USD USD balance 6392
            11 Feb, 2026 Transfer between balances - from USD to EUR -1450.00 USD USD balance 0
            06 Feb, 2026 Payment from Acme Corporation 409.5 USD USD balance 2650.62
            © 2005-2026 Payview, All Rights Reserved | www.payview.example.com
            Page 1 of 2
            """;

        var lines = StatementExtraction.TryExtractText(text, Categories);

        Assert.NotNull(lines);
        Assert.Equal(5, lines!.Count); // header block, column header, and footer all rejected
        // Signs decide direction: the statement writes debits with explicit minus, so an
        // unsigned positive is a credit (same convention as the CSV leg).
        Assert.Equal((new DateOnly(2026, 3, 3), "Reward from Payview", 250.00m, "income"),
            (lines[0].Date, lines[0].Description, lines[0].Amount, lines[0].Direction));
        // Neither the running-balance tail nor the payout-method column leaks anywhere.
        Assert.Equal(("Withdrawal to Banco Ejemplo (0976)", 6392.00m, "expense"),
            (lines[1].Description, lines[1].Amount, lines[1].Direction));
        Assert.Equal(("Payment from Acme Corporation", 6392.00m, "income"),
            (lines[2].Description, lines[2].Amount, lines[2].Direction));
        Assert.Equal(("Transfer between balances - from USD to EUR", 1450.00m, "expense"),
            (lines[3].Description, lines[3].Amount, lines[3].Direction));
        Assert.Equal(409.5m, lines[4].Amount); // single-decimal amounts parse too
    }

    [Theory]
    [InlineData("05 ene, 2026 PAGO RECIBIDO 100.00 MXN saldo 100", 2026, 1, 5)]
    [InlineData("28 dic 2025 COMPRA TIENDA -50.00 MXN saldo 50", 2025, 12, 28)]
    [InlineData("Sep 9, 2026 STORE PURCHASE -12.34 USD ref 001", 2026, 9, 9)]
    [InlineData("3 de enero de 2026 COMPRA TIENDA -50.00 MXN saldo 1", 2026, 1, 3)]
    [InlineData("31 de mayo del 2026 CARGO EJEMPLO -10.00 MXN saldo 1", 2026, 5, 31)]
    public void Text_MonthNameDates_ParseInEnglishAndSpanish(string line, int year, int month, int day)
    {
        var lines = StatementExtraction.TryExtractText(line, Categories);

        Assert.NotNull(lines);
        Assert.Equal(new DateOnly(year, month, day), lines![0].Date);
    }

    [Fact]
    public void Text_YearlessSpanishDates_AnchorToTheDocumentReferenceDate()
    {
        // A Mexican bank's "Envío de movimientos" export: per-line dates are "DD MMM" with NO year (the
        // year lives only in the request header), credits carry an explicit '+', charges are
        // unsigned, and an ATM line embeds the branch number right after the date — which once
        // parsed as the year 6472.
        const string text = """
            Envío de movimientos
            Hola, CASANDRA:
            Estos son los movimientos de tu
            Joy Bancodemo **123
            Solicitados el 11/05/2026 a las 15:35
            10 May gasolinera ejemplo mpos EN PROCESO $1,075.34
            03 May su abono...gracias +$1,300.00
            29 Abr netflixnme 110513pi3mx $369.00
            29 Mar 6472 suc playa ejemplo disp. efectivmx $3,000.00
            27 Dic su abono...gracias +$500.00
            1 de 1
            www.bancodemo.com
            """;

        var lines = StatementExtraction.TryExtractText(text, Categories);

        Assert.NotNull(lines);
        Assert.Equal(5, lines!.Count);
        // Yearless days anchor to the header's 11/05/2026 (most recent occurrence at or before it).
        Assert.Equal((new DateOnly(2026, 5, 10), "expense", 1075.34m),
            (lines[0].Date, lines[0].Direction, lines[0].Amount));
        // '+' marks the credits.
        Assert.Equal((new DateOnly(2026, 5, 3), "income", 1300m),
            (lines[1].Date, lines[1].Direction, lines[1].Amount));
        // The ATM branch number stays in the DESCRIPTION — it is not a year.
        Assert.Equal((new DateOnly(2026, 3, 29), "6472 suc playa ejemplo disp. efectivmx", "expense"),
            (lines[3].Date, lines[3].Description, lines[3].Direction));
        // December resolves to LAST year, not seven months into the future.
        Assert.Equal(new DateOnly(2025, 12, 27), lines[4].Date);
        // Nothing anywhere claims the year 6472.
        Assert.DoesNotContain(lines, l => l.Date.Year is < 1990 or > 2100);
    }

    [Fact]
    public void Text_YearlessDates_WithoutAReferenceDate_AreSkippedNotGuessed()
    {
        const string text = """
            10 May coffee place $12.00
            03 May su abono...gracias +$1,300.00
            """;

        Assert.Null(StatementExtraction.TryExtractText(text, Categories));
    }

    [Fact]
    public void Text_BalanceColumnTable_ParsesBlocksThroughTheBalanceChain()
    {
        // A Mexican "Detalle de Operaciones" balance-column statement (the real-world shape that
        // motivated this): yearless "04 MAY" dates anchored only by the Spanish cut-off date, every transaction a
        // multi-line BLOCK whose amounts land on its last line, the trailing number always the
        // running SALDO, an informational fee-exemption row that repeats the balance unchanged,
        // a block that straddles a page break full of furniture, and a Domiciliación annex that
        // repeats an amount without a balance. Only the balance chain reads this correctly.
        const string text = """
            Página 1 de 3
            000123.B04EXAMPLE.AR.0531.01
            MARIA EJEMPLO SOLIS
            AVENIDA SIEMPREVIVA 742
            00000 CIUDAD EJEMPLO, EDO.DEMO C.R.00001
            Estado de Cuenta
            Fecha de corte¹ 31 de mayo de 2026
            Período Del 1 al 31 de mayo del 2026
            Número de cuenta de cheques 00112234321
            Resumen Cuenta Ejemplo En Pesos Moneda Nacional
            Saldo anterior 1,000.00
            Depósitos 2,500.00
            Otros cargos 1,915.49
            Saldo al corte 1,584.51
            Detalle de Operaciones   En pesos Moneda Nacional
            FECHA CONCEPTO RETIROS DEPÓSITOS SALDO
            SALDO ANTERIOR 1,000.00
            04 MAY EXENCION COBRO COMISION
            PENALIZACION POR NO MANTENER
            IMPORTE A EX
            ENTAR $       500.00 1,000.00
            04 MAY TRASPASO REF 12345678 AUT.
            22222 SER BCA ELEC. 2,500.00 3,500.00
            11 MAY PAGO INTERBANCARIO A BANCO DOS
            AL BENEF. FULANO,EJEMPLO/PEREZ
            REF. 0110526   renta mayo
            MISMO DIA 1,500.00 2,000.00
            11 MAY CARGO CON IMPORTE CITADO
            EN EL TEXTO $ 99.99
            REFERENCIA 777 200.00 1,800.00
            28 MAY DOMI LUZ Y FUERZA
            MARIA EJEMPLO SOLIS
            Página 2 de 3
            Estado de Cuenta
            Detalle de Operaciones   En pesos Moneda Nacional
            FECHA CONCEPTO RETIROS DEPÓSITOS SALDO
            AUT DOMI 555001
            9990001122 215.49 1,584.51
            MARIA EJEMPLO SOLIS
            Página 3 de 3
            Estado de Cuenta
            Detalle de Operaciones   En pesos Moneda Nacional
            Domiciliación / Cargo Automático
            May 28 EMPRESA DE LUZ SA DE CV
            044000000 999 ABC931209XX9 $215.49
            "Este documento es una representación impresa sin validez fiscal"
            """;

        var lines = StatementExtraction.TryExtractText(text, Categories);

        Assert.NotNull(lines);
        Assert.Equal(4, lines!.Count);

        // The deposit needs no sign and no income keyword: the balance DELTA is the direction.
        Assert.Equal((new DateOnly(2026, 5, 4), "income", 2500m),
            (lines[0].Date, lines[0].Direction, lines[0].Amount));
        Assert.StartsWith("TRASPASO REF 12345678 AUT.", lines[0].Description);
        Assert.Contains("22222 SER BCA ELEC.", lines[0].Description);

        Assert.Equal((new DateOnly(2026, 5, 11), "expense", 1500m),
            (lines[1].Date, lines[1].Direction, lines[1].Amount));

        // An amount QUOTED in the description text stays description — the cell that reconciles
        // against the balance is the amount.
        Assert.Equal(("expense", 200m), (lines[2].Direction, lines[2].Amount));
        Assert.Contains("$ 99.99", lines[2].Description);

        // The page-break block: furniture (running titles, page numbers, column headers, the
        // customer's own name) stays out of the description.
        Assert.Equal((new DateOnly(2026, 5, 28), "expense", 215.49m),
            (lines[3].Date, lines[3].Direction, lines[3].Amount));
        Assert.Contains("DOMI LUZ Y FUERZA", lines[3].Description);
        Assert.Contains("AUT DOMI 555001", lines[3].Description);
        Assert.DoesNotContain("Página", lines[3].Description);
        Assert.DoesNotContain("FECHA CONCEPTO", lines[3].Description);
        Assert.DoesNotContain("MARIA", lines[3].Description);

        // The fee-exemption notice (balance unchanged) posts nothing — its quoted $500.00 is not
        // a transaction — and the Domiciliación annex repeats 215.49 without ever double-posting.
        Assert.DoesNotContain(lines, l => l.Amount == 500m);
        Assert.Equal(1, lines.Count(l => l.Amount == 215.49m));
        Assert.Equal(2500m, lines.Where(l => l.Direction == "income").Sum(l => l.Amount));
        Assert.Equal(1915.49m, lines.Where(l => l.Direction == "expense").Sum(l => l.Amount));
    }

    [Fact]
    public void Text_BalanceColumnTable_WithoutAnOpeningBalance_StaysNull()
    {
        // A balance-column header but no opening balance: the chain has no anchor, so the first
        // delta is unknowable. Honest null beats guessed directions.
        const string text = """
            FECHA CONCEPTO RETIROS DEPÓSITOS SALDO
            04 MAY TRASPASO REF 1 AUT.
            22222 SER BCA ELEC. 2,500.00 3,500.00
            """;

        Assert.Null(StatementExtraction.TryExtractText(text, Categories));
    }

    [Fact]
    public void Text_BalanceColumnTable_SingleLineRows_AmountIsTheCellNotTheBalance()
    {
        // The single-line variant of the same family: without the chain, the end-anchored
        // heuristic would read the trailing RUNNING BALANCE as the amount of every row.
        const string text = """
            EXAMPLE BANK
            Date Description Withdrawals Deposits Balance
            Beginning balance 4,200.00
            06/03/2026 PAYROLL ACME 2,500.00 6,700.00
            06/04/2026 GROCERY MART 82.45 6,617.55
            Ending balance 6,617.55
            """;

        var lines = StatementExtraction.TryExtractText(text, Categories);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal((new DateOnly(2026, 6, 3), "income", 2500m, "PAYROLL ACME"),
            (lines[0].Date, lines[0].Direction, lines[0].Amount, lines[0].Description));
        Assert.Equal((new DateOnly(2026, 6, 4), "expense", 82.45m, "GROCERY MART"),
            (lines[1].Date, lines[1].Direction, lines[1].Amount, lines[1].Description));
    }

    [Fact]
    public void Text_ColumnarFallback_RefusesMismatchedColumns()
    {
        // Three dates but two amounts: zipping would attach money to the wrong rows. Honest null.
        const string ocr = """
            2026-07-11

            2026-07-12

            2026-07-14

            SHOP A

            SHOP B

            SHOP C

            -10.00

            -20.00
            """;

        Assert.Null(StatementExtraction.TryExtractText(ocr, Categories));
    }
}
