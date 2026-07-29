using Networthy.Finance;
using Networthy.Finance.Persistence;

namespace Networthy.Finance.Tests;

/// <summary>
/// The deterministic account-identity leg of statement import: what the statement says about
/// itself (DetectAccountHint), when a hint may auto-attach to an existing account (MatchAccount —
/// only ever unambiguously), and the "did you mean" typo help (SuggestAccountNames).
/// </summary>
public sealed class AccountDetectionTests
{
    // ── DetectAccountHint ──

    [Fact]
    public void PdfHeader_InstitutionAndEndingIn_AreDetected()
    {
        const string text = """
            FIRST EXAMPLE BANK - STATEMENT

            Checking account ending in 4321
            2026-07-01  GROCERY MART  -45.67
            """;

        var hint = StatementExtraction.DetectAccountHint(text);

        Assert.NotNull(hint);
        Assert.Equal("First Example Bank", hint!.Institution);
        Assert.Equal("4321", hint.MaskLast4);
        Assert.Equal("First Example Bank ••••4321", hint.Label);
    }

    [Theory]
    [InlineData("Account ****9876 summary", "9876")]
    [InlineData("Cuenta ••••1111", "1111")]
    [InlineData("account no.: xx-xxx-2222", "2222")]
    [InlineData("Su cuenta termina en 3333.", "3333")]
    public void MaskedNumberPhrasings_YieldLastFour(string text, string expected)
    {
        Assert.Equal(expected, StatementExtraction.DetectAccountHint(text)?.MaskLast4);
    }

    [Fact]
    public void Ofx_OrgAndAcctId_WinOverHeaderHeuristics()
    {
        const string ofx = """
            OFXHEADER:100
            <OFX><SIGNONMSGSRSV1><SONRS><FI><ORG>Test Credit Union</ORG></FI></SONRS></SIGNONMSGSRSV1>
            <BANKACCTFROM><ACCTID>00-1234-005678</ACCTID></BANKACCTFROM>
            """;

        var hint = StatementExtraction.DetectAccountHint(ofx);

        Assert.NotNull(hint);
        Assert.Equal("Test Credit Union", hint!.Institution);
        Assert.Equal("5678", hint.MaskLast4); // trailing digits of ACCTID, mask style
    }

    [Fact]
    public void CsvHeader_IsNotMistakenForABankName()
    {
        const string csv = "Date,Description,Amount\n2026-07-01,WHOLE FOODS,-82.45\n";
        Assert.Null(StatementExtraction.DetectAccountHint(csv));
    }

    [Fact]
    public void CopyrightFooter_NamesTheInstitution_AndDominantCurrencyIsDetected()
    {
        // A statement whose header is all customer data: neither the address line, nor the
        // column-header row, nor the withdrawal target's "(0976)" may pose as this account's
        // identity — the © footer names the brand, and the amount tags name the currency.
        const string text = """
            Account Statement
            Casandra Ejemplo Perez Period 03/26/2025 - 03/26/2026
            CALLE FICTICIA 1234 COL CENTRO
            Date Description Amount Currency Payout Method Running Balance
            03 Mar, 2026 Reward from Payview 250.00 USD USD balance 250
            02 Mar, 2026 Withdrawal to Banco Ejemplo (0976) -6392.00 USD USD balance 0
            27 Feb, 2026 Transfer between balances - from USD to EUR -100.00 USD USD balance 100
            © 2005-2026 Payview, All Rights Reserved | www.payview.example.com
            """;

        var hint = StatementExtraction.DetectAccountHint(text);

        Assert.NotNull(hint);
        Assert.Equal("Payview", hint!.Institution);
        Assert.Null(hint.MaskLast4);
        Assert.Equal("USD", hint.Currency);
    }

    [Fact]
    public void GreetingHeadersAndWebFooter_YieldTheBrandAndShortMask()
    {
        // A Mexican bank's email export with no bank-titled header at all: greeting lines, a
        // "**123" 3-digit card mask, and the brand only in the web footer. None of the prose may
        // pose as the institution.
        const string text = """
            Envío de movimientos
            Hola, CASANDRA:
            Estos son los movimientos de tu
            Joy Bancodemo **123
            Solicitados el 11/05/2026 a las 15:35
            10 May gasolinera ejemplo mpos EN PROCESO $1,075.34
            www.bancodemo.com
            """;

        var hint = StatementExtraction.DetectAccountHint(text);

        Assert.NotNull(hint);
        Assert.Equal("Bancodemo", hint!.Institution);
        Assert.Equal("123", hint.MaskLast4);
    }

    [Fact]
    public void MexicanStatementHeader_YieldsChequesLastFourAndMxn_NeverTheCustomer()
    {
        // A Mexican bank-statement PDF header: the brand exists only as a LOGO (no text, no web
        // footer), the first title-shaped line is the CUSTOMER's name above their address, the
        // checking account is printed unmasked on a labeled line, and the currency is spelled
        // in words. The customer must not pose as the institution — honest null asks instead.
        const string text = """
            Página 1 de 2
            000123.B04EXAMPLE.AR.0531.01
            MARIA EJEMPLO SOLIS
            AVENIDA SIEMPREVIVA 742
            00000 CIUDAD EJEMPLO, EDO.DEMO C.R.00001
            Estado de Cuenta
            Fecha de corte 31 de mayo de 2026
            Número de cuenta de cheques 00112234321
            Resumen Cuenta Ejemplo En Pesos Moneda Nacional
            """;

        var hint = StatementExtraction.DetectAccountHint(text);

        Assert.NotNull(hint);
        Assert.Null(hint!.Institution);
        Assert.Equal("4321", hint.MaskLast4);
        Assert.Equal("MXN", hint.Currency);
        Assert.Equal("account ••••4321", hint.Label);
    }

    [Fact]
    public void DetectedLabel_IncludesCurrencyWhenKnown()
    {
        var batch = new StatementImportBatch
        {
            FileName = "x.pdf", Status = "needs-account",
            DetectedInstitution = "Payview", DetectedCurrency = "USD",
        };
        Assert.Equal("'Payview' (USD)", StatementImportTools.DetectedLabel(batch));
    }

    // ── MatchAccount ──

    private static Account Acct(string name, string? mask = null, string? institution = null, string currency = "USD") => new()
    {
        Name = name, Type = "checking", CurrencyCode = currency,
        MaskedAccountNumber = mask, InstitutionName = institution,
    };

    [Fact]
    public void UniqueMaskMatch_Assigns()
    {
        var accounts = new[] { Acct("Everyday", mask: "••••4321"), Acct("Savings", mask: "••••9999") };
        var match = StatementImportTools.MatchAccount(accounts, new DetectedAccountHint("Any Bank", "4321"));
        Assert.Equal("Everyday", match?.Name);
    }

    [Fact]
    public void AmbiguousMask_NeverGuesses()
    {
        var accounts = new[] { Acct("His card", mask: "••••4321"), Acct("Her card", mask: "••••4321") };
        Assert.Null(StatementImportTools.MatchAccount(accounts, new DetectedAccountHint(null, "4321")));
    }

    [Fact]
    public void InstitutionMatch_OnlyWhenSingle()
    {
        var accounts = new[]
        {
            Acct("Everyday", institution: "First Example Bank"),
            Acct("Unrelated", institution: "Other Bank"),
        };
        var match = StatementImportTools.MatchAccount(accounts, new DetectedAccountHint("First Example Bank", null));
        Assert.Equal("Everyday", match?.Name);

        // Two accounts at the same bank: the mask decides or nobody does.
        accounts = [Acct("A", institution: "First Example Bank"), Acct("B", institution: "First Example Bank")];
        Assert.Null(StatementImportTools.MatchAccount(accounts, new DetectedAccountHint("First Example Bank", null)));
    }

    [Fact]
    public void NoHint_NoMatch()
    {
        Assert.Null(StatementImportTools.MatchAccount([Acct("Everyday")], null));
    }

    // ── Currency as the tie-break (only ever narrows a set that already meant "ask") ──

    [Fact]
    public void SharedMask_DifferentCurrencies_TheStatementsCurrencyDecides()
    {
        // The same four digits on the household's MXN and USD accounts are two accounts, and a
        // statement tagged MXN says which one it is.
        var accounts = new[]
        {
            Acct("Everyday MXN", mask: "••••1234", currency: "MXN"),
            Acct("Dollar account", mask: "••••1234", currency: "USD"),
        };

        var match = StatementImportTools.MatchAccount(accounts, new DetectedAccountHint(null, "1234", "MXN"));

        Assert.Equal("Everyday MXN", match?.Name);
    }

    [Fact]
    public void SharedMask_SameCurrency_StillNeverGuesses()
    {
        var accounts = new[]
        {
            Acct("His card", mask: "••••4321", currency: "MXN"),
            Acct("Her card", mask: "••••4321", currency: "MXN"),
        };

        Assert.Null(StatementImportTools.MatchAccount(accounts, new DetectedAccountHint(null, "4321", "MXN")));
    }

    [Fact]
    public void SharedInstitution_DifferentCurrencies_TheStatementsCurrencyDecides()
    {
        var accounts = new[]
        {
            Acct("Pesos", institution: "Bancodemo", currency: "MXN"),
            Acct("Dollars", institution: "Bancodemo", currency: "USD"),
        };

        var match = StatementImportTools.MatchAccount(accounts, new DetectedAccountHint("Bancodemo", null, "USD"));

        Assert.Equal("Dollars", match?.Name);
    }

    [Fact]
    public void AmbiguousMask_NeverFallsThroughToTheInstitution()
    {
        // The old rule stopped dead at an ambiguous mask rather than letting a weaker signal
        // decide; adding a currency tie-break must not have opened that door.
        var accounts = new[]
        {
            Acct("His card", mask: "••••4321", institution: "Bancodemo", currency: "MXN"),
            Acct("Her card", mask: "••••4321", institution: "Bancodemo", currency: "MXN"),
        };

        Assert.Null(StatementImportTools.MatchAccount(accounts, new DetectedAccountHint("Bancodemo", "4321", "USD")));
    }

    [Fact]
    public void CurrencyAlone_NeverMatches()
    {
        // A currency is not an identity. Without a number or an institution, the flow must ask.
        var accounts = new[] { Acct("Only MXN account", currency: "MXN") };

        Assert.Null(StatementImportTools.MatchAccount(accounts, new DetectedAccountHint(null, null, "MXN")));
    }

    // ── Learning the number the human confirmed ──

    [Fact]
    public void AdoptDetectedIdentity_FillsBlanks_SoTheNextStatementMatches()
    {
        var account = Acct("Everyday spending");
        var batch = new StatementImportBatch
        {
            FileName = "june.pdf", Status = "needs-account", SourceFileId = Guid.NewGuid(),
            DetectedAccountMask = "••••1234", DetectedInstitution = "Bancodemo",
        };

        Assert.True(StatementImportTools.AdoptDetectedIdentity(account, batch));
        Assert.Equal("••••1234", account.MaskedAccountNumber);
        Assert.Equal("Bancodemo", account.InstitutionName);

        // And that is exactly what makes the next month's statement resolve itself.
        var match = StatementImportTools.MatchAccount([account], new DetectedAccountHint("Bancodemo", "1234", "MXN"));
        Assert.Equal("Everyday spending", match?.Name);
    }

    [Fact]
    public void AdoptDetectedIdentity_NeverOverwritesWhatTheHouseholdTyped()
    {
        var account = Acct("Everyday", mask: "••••9999", institution: "Typed By Hand");
        var batch = new StatementImportBatch
        {
            FileName = "june.pdf", Status = "needs-account", SourceFileId = Guid.NewGuid(),
            DetectedAccountMask = "••••1234", DetectedInstitution = "Bancodemo",
        };

        Assert.False(StatementImportTools.AdoptDetectedIdentity(account, batch));
        Assert.Equal("••••9999", account.MaskedAccountNumber);
        Assert.Equal("Typed By Hand", account.InstitutionName);
    }

    [Fact]
    public void AdoptDetectedIdentity_LearnsNothingFromAStatementThatIdentifiedNothing()
    {
        var account = Acct("Everyday");
        var batch = new StatementImportBatch { FileName = "anon.csv", Status = "needs-account", SourceFileId = Guid.NewGuid() };

        Assert.False(StatementImportTools.AdoptDetectedIdentity(account, batch));
        Assert.Null(account.MaskedAccountNumber);
        Assert.Null(account.InstitutionName);
    }

    // ── SuggestAccountNames ──

    [Fact]
    public void Suggestions_CoverTyposAndContainment()
    {
        var accounts = new[] { Acct("Guardrail Check"), Acct("Everyday spending"), Acct("Vacation fund") };

        Assert.Contains("Guardrail Check", StatementImportTools.SuggestAccountNames("Guardril Chek", accounts));
        Assert.Contains("Everyday spending", StatementImportTools.SuggestAccountNames("everyday", accounts));
        Assert.Empty(StatementImportTools.SuggestAccountNames("Cryptocurrency wallet", accounts));
    }
}
