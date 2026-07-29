using Networthy.Finance.Persistence;

namespace Networthy.Finance.Tests;

/// <summary>
/// The transfer matcher's promises (SPEC v2.1): conservative pairing of an expense in one
/// account with its income counterpart in another. Exactness is only "high confidence" when
/// unambiguous; cross-currency amounts are vouched for ONLY by the household's saved rates;
/// with no rate, both legs must say "transfer" in words; each transaction pairs at most once.
/// A wrong suggestion costs the trust the approval gate protects, so every edge here prefers
/// the miss.
/// </summary>
public class TransferMatchingTests
{
    private static readonly Account Payoneer = new()
    {
        Name = "Payoneer USD", Type = "checking", CurrencyCode = "USD", TenantId = Guid.NewGuid(),
    };

    private static readonly Account TaxMxn = new()
    {
        Name = "BBVA Tax MXN", Type = "checking", CurrencyCode = "MXN", TenantId = Payoneer.TenantId,
    };

    private static readonly Account BillsMxn = new()
    {
        Name = "Santander Bills MXN", Type = "checking", CurrencyCode = "MXN", TenantId = Payoneer.TenantId,
    };

    private static readonly IReadOnlyDictionary<Guid, Account> Accounts =
        new[] { Payoneer, TaxMxn, BillsMxn }.ToDictionary(a => a.Id);

    private static readonly IReadOnlyDictionary<string, decimal> NoRates =
        new Dictionary<string, decimal>();

    /// <summary>MXN rate saved by the household: 1 MXN = 0.052 USD (default currency USD).</summary>
    private static readonly IReadOnlyDictionary<string, decimal> MxnRate =
        new Dictionary<string, decimal> { ["MXN"] = 0.052m };

    private static Transaction Tx(Account account, string direction, decimal amount, string description, int day) =>
        new()
        {
            TenantId = account.TenantId,
            AccountId = account.Id,
            OccurredOn = new DateOnly(2026, 7, day),
            Amount = amount,
            CurrencyCode = account.CurrencyCode,
            Description = description,
            Direction = direction,
            Source = "upload",
        };

    [Fact]
    public void Same_currency_exact_amount_days_apart_is_one_high_confidence_pair()
    {
        var outgoing = Tx(TaxMxn, "expense", 5_000m, "Traspaso a Santander", 1);
        var incoming = Tx(BillsMxn, "income", 5_000m, "Deposito recibido", 3);

        var candidates = TransferMatching.FindCandidates(
            [outgoing, incoming], Accounts, NoRates, "USD");

        var pair = Assert.Single(candidates);
        Assert.True(pair.HighConfidence);
        Assert.Same(outgoing, pair.Outgoing);
        Assert.Same(incoming, pair.Incoming);
        Assert.Contains("2 days apart", pair.Reason);
    }

    [Fact]
    public void Legs_on_the_same_account_never_pair()
    {
        var candidates = TransferMatching.FindCandidates(
            [
                Tx(TaxMxn, "expense", 800m, "Retiro", 5),
                Tx(TaxMxn, "income", 800m, "Deposito", 5),
            ],
            Accounts, NoRates, "USD");

        Assert.Empty(candidates);
    }

    [Fact]
    public void The_date_window_is_minus_one_skew_day_through_five_days()
    {
        // Arrival 6 days later: outside the window.
        Assert.Empty(TransferMatching.FindCandidates(
            [Tx(TaxMxn, "expense", 900m, "Traspaso", 1), Tx(BillsMxn, "income", 900m, "Abono", 7)],
            Accounts, NoRates, "USD"));

        // Arrival stamped 2 days BEFORE the money left: beyond posting-date skew.
        Assert.Empty(TransferMatching.FindCandidates(
            [Tx(TaxMxn, "expense", 900m, "Traspaso", 5), Tx(BillsMxn, "income", 900m, "Abono", 3)],
            Accounts, NoRates, "USD"));

        // One day of skew is real bank behavior and pairs.
        Assert.Single(TransferMatching.FindCandidates(
            [Tx(TaxMxn, "expense", 900m, "Traspaso", 5), Tx(BillsMxn, "income", 900m, "Abono", 4)],
            Accounts, NoRates, "USD"));
    }

    [Fact]
    public void An_ambiguous_exact_amount_is_demoted_and_pairs_only_once()
    {
        var outgoing = Tx(TaxMxn, "expense", 1_200m, "Transferencia", 10);
        var first = Tx(BillsMxn, "income", 1_200m, "Deposito A", 11);
        var second = Tx(Payoneer, "income", 1_200m, "Deposit B", 11);
        second.CurrencyCode = "MXN"; // force a same-currency three-way tie on amount

        var candidates = TransferMatching.FindCandidates(
            [outgoing, first, second], Accounts, NoRates, "USD");

        // One outgoing leg can be one transfer: exactly one pair survives, demoted to possible.
        var pair = Assert.Single(candidates);
        Assert.False(pair.HighConfidence);
        Assert.Contains("several possible counterparts", pair.Reason);
    }

    [Fact]
    public void Cross_currency_amounts_agree_through_the_saved_rate()
    {
        // 1,000 USD out of Payoneer; 19,300 MXN lands two days later (19,300 × 0.052 = 1,003.60
        // USD — 0.36% from the leg that left, well inside tolerance).
        var outgoing = Tx(Payoneer, "expense", 1_000m, "Withdrawal to bank account", 1);
        var incoming = Tx(TaxMxn, "income", 19_300m, "SPEI PAYONEER MEXICO", 3);

        var candidates = TransferMatching.FindCandidates(
            [outgoing, incoming], Accounts, MxnRate, "USD");

        var pair = Assert.Single(candidates);
        Assert.True(pair.HighConfidence); // rate agreement + transfer-shaped descriptions
        Assert.Contains("saved exchange rate", pair.Reason);
    }

    [Fact]
    public void Cross_currency_rate_agreement_without_signals_is_only_possible_never_high()
    {
        var outgoing = Tx(Payoneer, "expense", 1_000m, "Monthly move", 1);
        var incoming = Tx(TaxMxn, "income", 19_300m, "Abono en cuenta", 2);

        var candidates = TransferMatching.FindCandidates(
            [outgoing, incoming], Accounts, MxnRate, "USD");

        var pair = Assert.Single(candidates);
        Assert.False(pair.HighConfidence);
    }

    [Fact]
    public void Cross_currency_amounts_off_the_saved_rate_do_not_pair()
    {
        // 19,300 MXN would need ~1,003 USD; 1,200 USD is 16% off — not this transfer.
        var candidates = TransferMatching.FindCandidates(
            [
                Tx(Payoneer, "expense", 1_200m, "Withdrawal to bank account", 1),
                Tx(TaxMxn, "income", 19_300m, "SPEI PAYONEER", 2),
            ],
            Accounts, MxnRate, "USD");

        Assert.Empty(candidates);
    }

    [Fact]
    public void Without_a_saved_rate_both_legs_must_say_transfer_in_words()
    {
        var outgoing = Tx(Payoneer, "expense", 1_000m, "Withdrawal to bank account", 1);
        var signaled = Tx(TaxMxn, "income", 19_300m, "SPEI PAYONEER", 2);
        var unsignaled = Tx(TaxMxn, "income", 19_300m, "Abono en cuenta", 2);

        var withBoth = TransferMatching.FindCandidates([outgoing, signaled], Accounts, NoRates, "USD");
        var pair = Assert.Single(withBoth);
        Assert.False(pair.HighConfidence); // never high on words alone
        Assert.Contains("no saved", pair.Reason);

        Assert.Empty(TransferMatching.FindCandidates([outgoing, unsignaled], Accounts, NoRates, "USD"));
    }

    [Fact]
    public void A_fee_sized_same_currency_difference_needs_a_transfer_shaped_description()
    {
        var outgoing = Tx(TaxMxn, "expense", 500m, "Transferencia a Santander", 1);
        var incoming = Tx(BillsMxn, "income", 495m, "Abono", 1); // 1% off: a wire fee

        var withSignal = TransferMatching.FindCandidates([outgoing, incoming], Accounts, NoRates, "USD");
        var pair = Assert.Single(withSignal);
        Assert.False(pair.HighConfidence);
        Assert.Contains("fee-sized", pair.Reason);

        // The same amounts with mute descriptions stay unmatched — 1% apart is also just
        // two different purchases.
        Assert.Empty(TransferMatching.FindCandidates(
            [Tx(TaxMxn, "expense", 500m, "Pago", 1), Tx(BillsMxn, "income", 495m, "Abono", 1)],
            Accounts, NoRates, "USD"));
    }

    [Fact]
    public void Transfer_signals_span_english_and_mexican_spanish()
    {
        Assert.True(TransferMatching.LooksLikeTransfer("SPEI ENVIADO BBVA"));
        Assert.True(TransferMatching.LooksLikeTransfer("Transferencia interbancaria"));
        Assert.True(TransferMatching.LooksLikeTransfer("Retiro de cuenta"));
        Assert.True(TransferMatching.LooksLikeTransfer("Withdrawal to bank account"));
        Assert.True(TransferMatching.LooksLikeTransfer("PAYONEER PAYOUT"));
        Assert.False(TransferMatching.LooksLikeTransfer("OXXO purchase"));
        Assert.False(TransferMatching.LooksLikeTransfer("Payroll deposit ACME"));
    }
}
