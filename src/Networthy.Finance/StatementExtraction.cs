using System.Globalization;
using System.Text.RegularExpressions;

namespace Networthy.Finance;

/// <summary>One extracted statement line, pending human review before it becomes a Transaction.</summary>
public sealed record ExtractedLine(
    DateOnly Date,
    string Description,
    decimal Amount,
    string Direction,
    string? SuggestedCategory);

/// <summary>
/// What a statement says about WHICH account it belongs to — the institution from the header
/// (or OFX &lt;ORG&gt;), the last four digits of a masked account number ("ending in 4321",
/// "****4321", OFX &lt;ACCTID&gt;), and the currency its amounts are tagged with ("-6392.00 USD").
/// Deterministic and best-effort: it feeds auto-matching and the "create this account?"
/// suggestion, and a human confirms before anything is created or posted.
/// </summary>
public sealed record DetectedAccountHint(string? Institution, string? MaskLast4, string? Currency = null)
{
    /// <summary>Human label, e.g. "First Example Bank ••••1234".</summary>
    public string Label => (Institution, MaskLast4) switch
    {
        ({ } bank, { } last4) => $"{bank} ••••{last4}",
        ({ } bank, null) => bank,
        (null, { } last4) => $"account ••••{last4}",
        _ => "an unidentified account",
    };
}

/// <summary>Lines plus whatever the document revealed about its account.</summary>
public sealed record StatementExtractionResult(
    IReadOnlyList<ExtractedLine> Lines, DetectedAccountHint? AccountHint);

/// <summary>
/// The document/AI leg of ADR-0004's hybrid extraction: statements the CSV/OFX templates can't
/// read (PDFs above all). The default registration is <see cref="ModelStatementExtractor"/> —
/// the platform's document reader extracts the text (digital PDFs keylessly; scanned ones
/// through the platform's OCR capability when configured), the household's own model parses it
/// under an arithmetic reconciliation gate, and the deterministic line parsers are the floor
/// when no model is available or its answer is rejected. A host can swap the whole leg with one
/// DI registration (<see cref="PlatformDocumentStatementExtractor"/> = deterministic only).
/// </summary>
public interface IStatementAiExtractor
{
    /// <summary>Extracted lines + account hint, or null when this extractor can't handle the content.</summary>
    public Task<StatementExtractionResult?> ExtractAsync(
        Guid fileId, string fileName, byte[] content, IReadOnlyList<string> categories, CancellationToken cancellationToken = default);
}

/// <summary>An explicit "no document leg" registration for hosts that want CSV/OFX only.</summary>
public sealed class NullStatementAiExtractor : IStatementAiExtractor
{
    public Task<StatementExtractionResult?> ExtractAsync(
        Guid fileId, string fileName, byte[] content, IReadOnlyList<string> categories, CancellationToken cancellationToken = default) =>
        Task.FromResult<StatementExtractionResult?>(null);
}

/// <summary>
/// The template leg of ADR-0004: deterministic extractors for the formats banks actually export —
/// CSV (with header sniffing and debit/credit-column support) and OFX/QFX. Pure functions,
/// unit-tested without files or a database. Category suggestions here are keyword-deterministic;
/// the LLM's judgment enters later, through the approval-gated categorize_transaction flow.
/// </summary>
public static partial class StatementExtraction
{
    /// <summary>Extracts from CSV content; null when no recognizable header row is found.</summary>
    public static IReadOnlyList<ExtractedLine>? TryExtractCsv(string content, IReadOnlyList<string> categories)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return null;
        }

        var header = SplitCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        int IndexOf(params string[] names) =>
            Array.FindIndex(header, h => names.Any(n => h.Contains(n, StringComparison.Ordinal)));

        var dateIdx = IndexOf("date");
        var descriptionIdx = IndexOf("description", "payee", "memo", "merchant", "details", "narrative");
        var amountIdx = IndexOf("amount");
        var debitIdx = IndexOf("debit", "withdrawal");
        var creditIdx = IndexOf("credit", "deposit");
        if (dateIdx < 0 || descriptionIdx < 0 || (amountIdx < 0 && debitIdx < 0 && creditIdx < 0))
        {
            return null;
        }

        var result = new List<ExtractedLine>();
        foreach (var raw in lines.Skip(1))
        {
            var fields = SplitCsvLine(raw);
            if (fields.Count <= Math.Max(dateIdx, descriptionIdx))
            {
                continue;
            }

            if (!TryParseDate(fields[dateIdx].Trim(), out var date))
            {
                continue;
            }

            var description = fields[descriptionIdx].Trim();
            decimal amount;
            string direction;
            if (amountIdx >= 0 && fields.Count > amountIdx && TryParseAmount(fields[amountIdx], out var signed))
            {
                // Bank convention: negative = money out.
                direction = signed < 0 ? "expense" : "income";
                amount = Math.Abs(signed);
            }
            else if (debitIdx >= 0 && fields.Count > debitIdx && TryParseAmount(fields[debitIdx], out var debit) && debit != 0)
            {
                direction = "expense";
                amount = Math.Abs(debit);
            }
            else if (creditIdx >= 0 && fields.Count > creditIdx && TryParseAmount(fields[creditIdx], out var credit) && credit != 0)
            {
                direction = "income";
                amount = Math.Abs(credit);
            }
            else
            {
                continue;
            }

            if (amount == 0 || description.Length == 0)
            {
                continue;
            }

            result.Add(new ExtractedLine(date, description, amount, direction, SuggestCategory(description, categories)));
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>Extracts from OFX/QFX content; null when no STMTTRN blocks are found.</summary>
    public static IReadOnlyList<ExtractedLine>? TryExtractOfx(string content, IReadOnlyList<string> categories)
    {
        var blocks = StmtTrnPattern().Matches(content);
        if (blocks.Count == 0)
        {
            return null;
        }

        var result = new List<ExtractedLine>();
        foreach (Match block in blocks)
        {
            var body = block.Groups[1].Value;
            string? Tag(string name)
            {
                var m = Regex.Match(body, $"<{name}>([^<\r\n]+)", RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }

            var posted = Tag("DTPOSTED");
            var amountText = Tag("TRNAMT");
            var name = Tag("NAME") ?? Tag("MEMO");
            if (posted is null || amountText is null || name is null || posted.Length < 8)
            {
                continue;
            }

            if (!DateOnly.TryParseExact(posted[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !TryParseAmount(amountText, out var signed) || signed == 0)
            {
                continue;
            }

            result.Add(new ExtractedLine(
                date, name, Math.Abs(signed),
                signed < 0 ? "expense" : "income",
                SuggestCategory(name, categories)));
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Extracts from free text (a PDF statement's extracted text): one transaction per line that
    /// carries both a parseable date and a trailing money amount. Sign convention matches the
    /// CSV leg (negative/parentheses = money out); unsigned amounts default to expense — the
    /// human review gate exists precisely to correct layout heuristics. Header/summary lines
    /// (balance, total, page …) are skipped. Null when nothing parses.
    /// </summary>
    public static IReadOnlyList<ExtractedLine>? TryExtractText(string text, IReadOnlyList<string> categories)
    {
        // Anchors yearless "29 Mar" lines (Mexican banks often print the year only in the header).
        var reference = DocumentReferenceDate(text);

        var parsed = new List<(DateOnly Date, string Description, decimal Signed, bool PlusSigned)>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length < 8 || SkipTextLine(line))
            {
                continue;
            }

            var dateMatch = TextDatePattern().Match(line);
            if (!dateMatch.Success)
            {
                continue;
            }
            if (!TryResolveLineDate(dateMatch.Value, reference, out var date))
            {
                // A yearless day+month resolves against the document's reference date; without a
                // reference the line is honestly skipped rather than guessed into some year.
                continue;
            }

            // The amount lives either at the END of the line (classic "DESC      -82.45"
            // layouts) or MID-LINE tagged with a currency code ("Withdrawal to X -6392.00 USD
            // USD balance 0", where the line's last token is a running balance, not the
            // amount). The tagged spelling is unambiguous, so it wins.
            string amountToken;
            int amountIndex;
            var tagged = CurrencyAmountPattern().Match(line);
            if (tagged.Success)
            {
                amountToken = tagged.Groups[1].Value;
                amountIndex = tagged.Groups[1].Index;
            }
            else if (TextAmountPattern().Matches(line).LastOrDefault() is { } trailing)
            {
                amountToken = trailing.Value;
                amountIndex = trailing.Index;
            }
            else
            {
                continue;
            }

            if (!TryParseAmount(amountToken, out var signed) || signed == 0)
            {
                continue;
            }

            // Tabular rows read Date | Description | Amount | trailing columns — the
            // description is the segment between the date and the amount, so running-balance
            // and payout-method tails never leak into it.
            var dateEnd = dateMatch.Index + dateMatch.Length;
            var description = (amountIndex > dateEnd
                    ? line[dateEnd..amountIndex]
                    : line.Remove(amountIndex, amountToken.Length).Replace(dateMatch.Value, ""))
                .Trim(' ', '\t', '-', '·', '|', '*');
            if (description.Length == 0)
            {
                continue;
            }

            parsed.Add((date, description, signed, amountToken.TrimStart().StartsWith('+')));
        }

        // Direction: an explicit sign is authoritative. An UNSIGNED positive is income when the
        // statement spells its debits with explicit minus signs (the signed-column convention,
        // same as the CSV leg) — otherwise only the income-keyword heuristic vouches for it.
        var signedColumns = parsed.Any(p => p.Signed < 0);
        var result = parsed
            .Select(p => new ExtractedLine(
                p.Date, p.Description, Math.Abs(p.Signed),
                p.Signed < 0 ? "expense"
                    : p.PlusSigned || signedColumns || LooksLikeIncome(p.Description) ? "income"
                    : "expense",
                SuggestCategory(p.Description, categories)))
            .ToList();

        // Row-wise found nothing? OCR of a tabular statement often reads COLUMNS as blocks —
        // every date, then every description, then every amount. Reconstruct the rows.
        return result.Count > 0 ? result : TryExtractColumnarText(text, categories);
    }

    /// <summary>A full date, or a yearless day+month anchored to the document's reference date.</summary>
    private static bool TryResolveLineDate(string token, DateOnly? reference, out DateOnly date)
    {
        if (TryParseDate(token, out date))
        {
            return true;
        }

        if (reference is { } anchor && TryParseDayMonth(token, out var day, out var month)
            && TryResolveYearless(day, month, anchor, out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    /// <summary>
    /// The columnar fallback: Tesseract-style OCR frequently emits a table as three runs of
    /// same-kind lines (dates, descriptions, amounts) in row order. When the date and amount
    /// runs agree on a count and exactly that many description lines sit between or after them,
    /// zip them back into rows. Anything that doesn't line up returns null — the review gate
    /// wants honest nothing over shuffled somethings. Amount lines get OCR-noise cleaning
    /// ("-19:°.99" → "-19.99", "+800 .00" → "+800.00"); a single unreadable row is skipped
    /// without sinking its siblings.
    /// </summary>
    private static IReadOnlyList<ExtractedLine>? TryExtractColumnarText(string text, IReadOnlyList<string> categories)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        var kinds = lines.Select(Classify).ToArray();

        var dateRun = LongestRun(kinds, LineKind.Date);
        var amountRun = LongestRun(kinds, LineKind.Amount);
        if (dateRun.Length < 2 || dateRun.Length != amountRun.Length)
        {
            return null;
        }

        var (firstRun, secondRun) = dateRun.Start < amountRun.Start ? (dateRun, amountRun) : (amountRun, dateRun);
        var between = TextLinesIn(lines, kinds, firstRun.Start + firstRun.Length, secondRun.Start);
        var after = TextLinesIn(lines, kinds, secondRun.Start + secondRun.Length, lines.Length);
        var descriptions = between.Count == dateRun.Length ? between
            : after.Count == dateRun.Length ? after
            : null;
        if (descriptions is null)
        {
            return null;
        }

        var result = new List<ExtractedLine>();
        for (var i = 0; i < dateRun.Length; i++)
        {
            var amountToken = CleanOcrAmount(lines[amountRun.Start + i]);
            if (!TryParseDate(TextDatePattern().Match(lines[dateRun.Start + i]).Value, out var date)
                || !TryParseAmount(amountToken, out var signed) || signed == 0)
            {
                continue;
            }

            var description = descriptions[i];
            var direction = signed < 0 ? "expense"
                : LooksLikeIncome(description) || amountToken.StartsWith('+') ? "income"
                : "expense";
            result.Add(new ExtractedLine(
                date, description, Math.Abs(signed), direction,
                SuggestCategory(description, categories)));
        }

        return result.Count > 0 ? result : null;
    }

    private enum LineKind { Date, Amount, Text }

    private static LineKind Classify(string line)
    {
        var dateMatch = TextDatePattern().Match(line);
        if (dateMatch.Success && line.Length <= dateMatch.Length + 2 && !TextAmountPattern().IsMatch(line))
        {
            return LineKind.Date;
        }

        var cleaned = CleanOcrAmount(line);
        if (line.Length <= 24 && cleaned.Length > 0
            && TextAmountPattern().IsMatch(cleaned) && TryParseAmount(cleaned, out _))
        {
            return LineKind.Amount;
        }

        return LineKind.Text;
    }

    private static (int Start, int Length) LongestRun(LineKind[] kinds, LineKind kind)
    {
        var (bestStart, bestLength) = (-1, 0);
        for (var i = 0; i < kinds.Length;)
        {
            if (kinds[i] != kind)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < kinds.Length && kinds[i] == kind)
            {
                i++;
            }
            if (i - start > bestLength)
            {
                (bestStart, bestLength) = (start, i - start);
            }
        }
        return (bestStart, bestLength);
    }

    private static List<string> TextLinesIn(string[] lines, LineKind[] kinds, int from, int to)
    {
        var result = new List<string>();
        for (var i = Math.Max(from, 0); i < to && i < lines.Length; i++)
        {
            if (kinds[i] == LineKind.Text && !SkipTextLine(lines[i]))
            {
                result.Add(lines[i]);
            }
        }
        return result;
    }

    /// <summary>Strips OCR artifacts from a token that should be an amount (keeps digits and money punctuation).</summary>
    private static string CleanOcrAmount(string token) =>
        new([.. token.Where(c => char.IsAsciiDigit(c) || c is '.' or ',' or '+' or '-' or '(' or ')')]);

    private static bool LooksLikeIncome(string description)
    {
        string[] hints = ["deposit", "payroll", "salary", "direct dep", "interest", "payment received", "refund", "income",
            "abono", "depósito", "deposito", "nómina", "nomina", "reembolso"];
        return hints.Any(h => description.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SkipTextLine(string line)
    {
        // Anchored at line START: these words open summary/footer rows ("Beginning balance …",
        // "Page 1 of 2"), but a real transaction row may CONTAIN them mid-line ("… USD balance 0"
        // is how Payoneer prints its running-balance column) — those must reach the parser.
        string[] noise = ["balance", "total", "statement", "page ", "account number", "beginning", "ending", "summary", "subtotal", "opening", "closing"];
        return noise.Any(n => line.StartsWith(n, StringComparison.OrdinalIgnoreCase));
    }

    // Years are REGEX-bounded to 19xx/20xx: without the bound, "29 Mar 6472" (a day, a month, and
    // an ATM branch number) parses as the year 6472 — a real statement line did exactly that. The
    // "31 de mayo de(l) 2026" alternative is how Mexican statements spell their cut-off dates.
    // The final alternative matches a YEARLESS "29 Mar" (banks that print the year only in the
    // header); the lookahead keeps it from half-eating a dated form, and the caller resolves the
    // year against the document's reference date.
    [GeneratedRegex(@"\b((?:19|20)\d{2}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/(?:19|20)\d{2}|\d{1,2}\s+de\s+\p{L}{3,10}\.?\s+(?:de|del)\s+(?:19|20)\d{2}|\d{1,2}\s+\p{L}{3,10}\.?,?\s+(?:19|20)\d{2}|\p{L}{3,10}\.?\s+\d{1,2},?\s+(?:19|20)\d{2}|\d{1,2}\s+\p{L}{3,10}\.?(?!,?\s+(?:19|20)\d{2}))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TextDatePattern();

    /// <summary>Dates that carry their own year — scanned document-wide to anchor yearless lines.</summary>
    [GeneratedRegex(@"\b((?:19|20)\d{2}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/(?:19|20)\d{2}|\d{1,2}\s+de\s+\p{L}{3,10}\.?\s+(?:de|del)\s+(?:19|20)\d{2}|\d{1,2}\s+\p{L}{3,10}\.?,?\s+(?:19|20)\d{2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex FullDatePattern();

    [GeneratedRegex(@"\(?[+-]?\$?\d{1,3}(,\d{3})*\.\d{2}\)?-?(\s?(CR|DR))?$", RegexOptions.IgnoreCase)]
    private static partial Regex TextAmountPattern();

    /// <summary>
    /// An amount explicitly tagged with an ISO currency code ("-6392.00 USD", "1,234.56 MXN") —
    /// the unambiguous spelling statements use when other numeric columns (running balance,
    /// reference numbers) share the line. Group 1 is the amount, group 2 the code. Codes are
    /// matched case-SENSITIVELY so prose words never qualify.
    /// </summary>
    [GeneratedRegex(@"(?<![\w.,])([+-]?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?|\((?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?\))\s?(USD|EUR|MXN|GBP|CAD|AUD|NZD|JPY|CHF|CNY|BRL|COP|ARS|CLP|PEN|UYU|DKK|SEK|NOK|PLN|CZK|INR|KRW|ZAR)\b")]
    private static partial Regex CurrencyAmountPattern();

    /// <summary>
    /// Deterministic category suggestion: the household category whose name appears in the
    /// description (or a small merchant-keyword table mapping to standard categories). A
    /// suggestion is only a suggestion — the human approves the batch either way.
    /// </summary>
    public static string? SuggestCategory(string description, IReadOnlyList<string> categories)
    {
        foreach (var category in categories)
        {
            if (description.Contains(category, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        foreach (var (keyword, category) in MerchantKeywords)
        {
            if (description.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return null;
    }

    private static readonly (string Keyword, string Category)[] MerchantKeywords =
    [
        ("grocery", "Groceries"), ("supermarket", "Groceries"), ("market", "Groceries"),
        ("restaurant", "Dining"), ("cafe", "Dining"), ("coffee", "Dining"), ("pizza", "Dining"),
        ("uber", "Transportation"), ("lyft", "Transportation"), ("gas", "Transportation"), ("fuel", "Transportation"),
        ("pharmacy", "Health"), ("clinic", "Health"), ("doctor", "Health"),
        ("netflix", "Subscriptions"), ("spotify", "Subscriptions"), ("subscription", "Subscriptions"),
        ("rent", "Housing"), ("mortgage", "Housing"),
        ("electric", "Utilities"), ("water", "Utilities"), ("internet", "Utilities"),
        ("payroll", "Salary"), ("salary", "Salary"), ("direct dep", "Salary"),
        ("airline", "Travel"), ("hotel", "Travel"),
    ];

    private static bool TryParseDate(string text, out DateOnly date)
    {
        string[] formats = ["yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy", "yyyyMMdd", "MMM d, yyyy"];
        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return PlausibleYear(date);
            }
        }

        // "03 Mar, 2026" / "3 ene 2026" / "Mar 3, 2026" — month names in English or Spanish,
        // resolved by 3-letter prefix so the host's locale never matters. Spanish connectives
        // drop first, so "31 de mayo del 2026" reads as its three real tokens. Unknown words
        // simply fail here, which is also what rejects date-shaped phrases the regex over-matched.
        var tokens = text.Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("de", StringComparison.OrdinalIgnoreCase)
                        && !t.Equals("del", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (tokens.Length == 3)
        {
            var (dayToken, monthToken) = char.IsLetter(tokens[0][0]) ? (tokens[1], tokens[0]) : (tokens[0], tokens[1]);
            if (monthToken.Length >= 3 && MonthNamesByPrefix.TryGetValue(monthToken[..3], out var month)
                && int.TryParse(dayToken, out var day)
                && int.TryParse(tokens[2], out var year)
                && year is >= 1990 and <= 2100
                && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            {
                date = new DateOnly(year, month, day);
                return true;
            }
        }

        // Deliberately NO permissive DateOnly.TryParse fallback: invariant parsing accepts
        // "29 Mar 6472" as the year 6472 (an ATM branch number) and "10 May" as
        // 2010-05-01 (year-month form). Every accepted shape is listed explicitly above;
        // yearless day+month forms go through TryParseDayMonth + the document reference instead.
        date = default;
        return false;
    }

    private static bool PlausibleYear(DateOnly date) => date.Year is >= 1990 and <= 2100;

    /// <summary>"29 Mar" / "21 abr." — a day and a month name with NO year on the line.</summary>
    private static bool TryParseDayMonth(string text, out int day, out int month)
    {
        day = 0;
        month = 0;
        var tokens = text.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 2
               && int.TryParse(tokens[0], out day) && day is >= 1 and <= 31
               && tokens[1].Length >= 3 && MonthNamesByPrefix.TryGetValue(tokens[1][..3], out month);
    }

    /// <summary>
    /// The document's own "as of" moment: the latest plausible fully-dated value anywhere in the
    /// text (period headers, "Solicitados el 11/05/2026", issue dates). Yearless statement lines
    /// resolve against it — banks list the PAST, so a month/day lands on its most recent
    /// occurrence at or before this date.
    /// </summary>
    private static DateOnly? DocumentReferenceDate(string text)
    {
        DateOnly? reference = null;
        foreach (Match match in FullDatePattern().Matches(text))
        {
            if (TryParseDate(match.Value, out var date) && (reference is null || date > reference))
            {
                reference = date;
            }
        }

        return reference;
    }

    private static bool TryResolveYearless(int day, int month, DateOnly reference, out DateOnly date)
    {
        // Most recent occurrence of this month/day at or before the reference: "27 Dic" on a
        // statement requested 2026-05-11 is December LAST year, not a date seven months ahead.
        for (var year = reference.Year; year >= reference.Year - 1; year--)
        {
            if (day <= DateTime.DaysInMonth(year, month))
            {
                var candidate = new DateOnly(year, month, day);
                if (candidate <= reference)
                {
                    date = candidate;
                    return true;
                }
            }
        }

        date = default;
        return false;
    }

    /// <summary>English + Spanish month-name prefixes (full names share them: "marzo" → "mar").</summary>
    private static readonly Dictionary<string, int> MonthNamesByPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["ene"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["abr"] = 4,
        ["may"] = 5, ["jun"] = 6, ["jul"] = 7, ["aug"] = 8, ["ago"] = 8, ["sep"] = 9,
        ["set"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12, ["dic"] = 12,
    };

    private static bool TryParseAmount(string text, out decimal amount)
    {
        var cleaned = text.Trim().Replace("$", "").Replace(",", "");
        // Parentheses are the accounting spelling of negative.
        if (cleaned.StartsWith('(') && cleaned.EndsWith(')'))
        {
            cleaned = "-" + cleaned[1..^1];
        }

        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out amount);
    }

    /// <summary>Splits one CSV line honoring double-quoted fields (embedded commas + doubled quotes).</summary>
    internal static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    [GeneratedRegex("<STMTTRN>(.*?)</STMTTRN>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StmtTrnPattern();

    // ── Account detection (which account does this statement BELONG to?) ──

    /// <summary>
    /// Best-effort, deterministic detection of the statement's own account identity, shared by
    /// every format leg: OFX tags when present, else masked-number phrasing and the header line.
    /// Null when the text says nothing usable — the review flow then asks instead of guessing.
    /// </summary>
    public static DetectedAccountHint? DetectAccountHint(string text)
    {
        string? last4 = null;
        // OFX is explicit about the account; trailing digits of <ACCTID> are the mask every
        // bank prints. Otherwise fall back to how statements SAY it: "ending in 4321",
        // "****4321" / "••••4321" / "xxxx4321", or "Account #: ...4321".
        var acctId = OfxAcctIdPattern().Match(text);
        if (acctId.Success)
        {
            var digits = new string(acctId.Groups[1].Value.Where(char.IsAsciiDigit).ToArray());
            last4 = digits.Length >= 4 ? digits[^4..] : null;
        }
        last4 ??= FirstGroup(EndingInPattern().Match(text)) ?? FirstGroup(MaskedNumberPattern().Match(text));
        // Mexican statements print the checking account UNMASKED on a labeled line ("Número de
        // cuenta de cheques 70177434092") — its trailing four digits are the same identity every
        // mask spelling would carry.
        if (last4 is null && AccountNumberLinePattern().Match(text) is { Success: true } acct
            && acct.Groups[1].Value is { Length: >= 4 } number)
        {
            last4 = number[^4..];
        }

        var institution = OfxOrgPattern().Match(text) is { Success: true } org
            ? org.Groups[1].Value.Trim()
            : DetectInstitutionFromHeader(text);

        var currency = DetectDominantCurrency(text);
        return last4 is null && institution is null && currency is null
            ? null
            : new DetectedAccountHint(institution, last4, currency);
    }

    /// <summary>
    /// The statement's own currency: the ISO code its amounts are tagged with, when one code
    /// clearly dominates (at least two amounts, at least 80% of all tagged amounts). Feeds the
    /// created-account currency so a USD statement never lands in a household-default account.
    /// </summary>
    private static string? DetectDominantCurrency(string text)
    {
        var counts = CurrencyAmountPattern().Matches(text)
            .GroupBy(m => m.Groups[2].Value)
            .Select(g => (Code: g.Key, Count: g.Count()))
            .ToList();
        if (counts.Count == 0)
        {
            // Mexican statements rarely tag amounts with ISO codes — they say it in words
            // ("En pesos Moneda Nacional").
            return MonedaNacionalPattern().IsMatch(text) ? "MXN" : null;
        }

        var total = counts.Sum(c => c.Count);
        var top = counts.MaxBy(c => c.Count);
        return top.Count >= 2 && top.Count * 10 >= total * 8 ? top.Code : null;
    }

    /// <summary>
    /// The bank name is almost always the first title-ish line of a rendered statement. Skip
    /// anything that reads as data (CSV headers, dates, amounts) and normalize the shouting
    /// ("FIRST EXAMPLE BANK - STATEMENT" → "First Example Bank").
    /// </summary>
    private static string? DetectInstitutionFromHeader(string text)
    {
        var headerLines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < headerLines.Length && i < 8; i++)
        {
            var line = headerLines[i].Trim();
            if (line.Length is < 3 or > 64
                || line.Count(c => c == ',') >= 2                       // a CSV header, not a title
                || line.Contains('<')                                    // markup
                || char.IsDigit(line[0])
                || line.Count(char.IsDigit) >= 3                         // street address / id line
                || line.EndsWith(':')                                    // a label or a greeting
                || line.Contains("www.", StringComparison.OrdinalIgnoreCase) // a URL is a footer, not a title
                || line.Contains("http", StringComparison.OrdinalIgnoreCase)
                || GreetingWords.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase))
                || TextDatePattern().IsMatch(line)
                || TextAmountPattern().IsMatch(line)
                || ColumnHeaderWords.Count(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)) >= 2
                || line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6 // a sentence, not a title
                || LooksLikeAddressee(headerLines, i)
                || line.Count(char.IsLetter) < 3)
            {
                continue;
            }

            // "FIRST EXAMPLE BANK - STATEMENT" / "… ACCOUNT STATEMENT" → keep the bank part.
            var title = StatementSuffixPattern().Replace(line, "").Trim(' ', '-', '—', ':');
            if (title.Count(char.IsLetter) < 3)
            {
                continue;
            }

            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title.ToLowerInvariant());
        }

        // No usable title line — many statements identify the brand only in the copyright footer
        // ("© 2005-2026 Payoneer, All Rights Reserved").
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var match = CopyrightBrandPattern().Match(raw);
            if (match.Success && match.Groups[1].Value.Trim().Count(char.IsLetter) >= 3)
            {
                return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups[1].Value.Trim().ToLowerInvariant());
            }
        }

        // Still nothing — the bank's web footer is the last-resort brand ("www.bancodemo.com").
        if (WebDomainPattern().Match(text) is { Success: true } domain)
        {
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(domain.Groups[1].Value.ToLowerInvariant());
        }

        return null;
    }

    /// <summary>Words that, two or more together, read as a table's column header, not a bank name.</summary>
    private static readonly string[] ColumnHeaderWords =
        ["date", "description", "amount", "currency", "balance", "debit", "credit", "payee", "fecha", "importe", "saldo"];

    /// <summary>A line addressed to the CUSTOMER is never the bank's name ("Hola, ABRAHAM:").</summary>
    private static readonly string[] GreetingWords =
        ["hola", "hello", "dear ", "estimado", "estimada", "estos son", "these are"];

    /// <summary>
    /// The window-envelope block: a statement's first lines carry the CUSTOMER's name with the
    /// street address right under it. A title-shaped line whose next line or two reads as an
    /// address (digit-heavy, no statement vocabulary) is the addressee, never the bank.
    /// </summary>
    private static bool LooksLikeAddressee(string[] lines, int index)
    {
        for (var j = index + 1; j <= index + 2 && j < lines.Length; j++)
        {
            var next = lines[j].Trim();
            if (next.Length == 0)
            {
                continue;
            }

            if (next.Count(char.IsDigit) >= 4 && next.Count(char.IsLetter) >= 3
                && !StatementVocabulary.Any(w => next.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Words a statement line uses about ITSELF — their presence means "not a mailing address".</summary>
    private static readonly string[] StatementVocabulary =
    [
        "account", "cuenta", "statement", "estado", "period", "período", "periodo",
        "ending", "termina", "page", "página", "member fdic",
    ];

    private static string? FirstGroup(Match match) => match.Success ? match.Groups[1].Value : null;

    [GeneratedRegex(@"<ACCTID>\s*([A-Za-z0-9*\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex OfxAcctIdPattern();

    [GeneratedRegex(@"<ORG>\s*([^<\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex OfxOrgPattern();

    [GeneratedRegex(@"\b(?:ending|termina)\s+(?:in|en)\s+(\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EndingInPattern();

    // {3,4} trailing digits: some Mexican banks mask cards as "**877" — three visible digits, not four.
    [GeneratedRegex(@"(?:[*•xX]{2,}|(?:account|acct|cuenta)\s*(?:no\.?|number|núm\.?|#)?\s*[:#]\s*[*•xX\d\- ]*?)[\- ]?(\d{3,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex MaskedNumberPattern();

    /// <summary>An unmasked, labeled account-number line ("Número de cuenta de cheques 70177434092").</summary>
    [GeneratedRegex(@"(?:n[úu]mero\s+de\s+cuenta(?:\s+de\s+cheques)?|cuenta\s+de\s+cheques)\s*(?:no\.?|#)?\s*[:#]?\s*(\d{7,20})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AccountNumberLinePattern();

    /// <summary>How Mexican statements declare their currency in words instead of ISO tags.</summary>
    [GeneratedRegex(@"\bmoneda\s+nacional\b|\ben\s+pesos\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonedaNacionalPattern();

    [GeneratedRegex(@"\b(?:account\s+)?(?:statement|estado\s+de\s+cuenta|env[ií]o\s+de\s+movimientos|movimientos)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex StatementSuffixPattern();

    /// <summary>The bank's own web footer ("www.bancodemo.com") — the last-resort brand signal.</summary>
    [GeneratedRegex(@"\bwww\.([a-z0-9-]{3,40})\.(?:com\.mx|com|mx|net|org)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WebDomainPattern();

    [GeneratedRegex(@"(?:©|\(c\)|copyright)\s*(?:\d{4}\s*[-–—]\s*\d{4}|\d{4})?\s*([\p{L}][\p{L}\p{N} .&'-]{1,40}?)\s*,?\s+(?:all\s+rights\s+reserved|todos\s+los\s+derechos)", RegexOptions.IgnoreCase)]
    private static partial Regex CopyrightBrandPattern();
}
