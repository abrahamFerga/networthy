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
/// The document/AI leg of ADR-0004's hybrid extraction: statements the CSV/OFX templates can't
/// read (PDFs above all). The default registration is
/// <see cref="PlatformDocumentStatementExtractor"/> — the platform's document reader extracts
/// the text (digital PDFs keylessly; scanned ones through the platform's OCR capability, e.g.
/// Azure Document Intelligence, when the deployment configures it), then the deterministic
/// line parser takes over. A host can swap the whole leg with one DI registration.
/// </summary>
public interface IStatementAiExtractor
{
    /// <summary>Extracted lines, or null when this extractor can't handle the content.</summary>
    public Task<IReadOnlyList<ExtractedLine>?> ExtractAsync(
        Guid fileId, string fileName, byte[] content, IReadOnlyList<string> categories, CancellationToken cancellationToken = default);
}

/// <summary>An explicit "no document leg" registration for hosts that want CSV/OFX only.</summary>
public sealed class NullStatementAiExtractor : IStatementAiExtractor
{
    public Task<IReadOnlyList<ExtractedLine>?> ExtractAsync(
        Guid fileId, string fileName, byte[] content, IReadOnlyList<string> categories, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExtractedLine>?>(null);
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
        var result = new List<ExtractedLine>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length < 8 || SkipTextLine(line))
            {
                continue;
            }

            var dateMatch = TextDatePattern().Match(line);
            if (!dateMatch.Success || !TryParseDate(dateMatch.Value, out var date))
            {
                continue;
            }

            var amountMatch = TextAmountPattern().Matches(line).LastOrDefault();
            if (amountMatch is null || !TryParseAmount(amountMatch.Value, out var signed) || signed == 0)
            {
                continue;
            }

            var description = line
                .Remove(amountMatch.Index, amountMatch.Length)
                .Replace(dateMatch.Value, "")
                .Trim(' ', '\t', '-', '·', '|', '*');
            if (description.Length == 0)
            {
                continue;
            }

            var direction = signed < 0 ? "expense"
                : LooksLikeIncome(description) ? "income"
                : "expense";
            result.Add(new ExtractedLine(
                date, description, Math.Abs(signed), direction,
                SuggestCategory(description, categories)));
        }

        return result.Count > 0 ? result : null;
    }

    private static bool LooksLikeIncome(string description)
    {
        string[] hints = ["deposit", "payroll", "salary", "direct dep", "interest", "payment received", "refund"];
        return hints.Any(h => description.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SkipTextLine(string line)
    {
        string[] noise = ["balance", "total", "statement", "page ", "account number", "beginning", "ending", "summary"];
        return noise.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\b(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4})\b")]
    private static partial Regex TextDatePattern();

    [GeneratedRegex(@"\(?-?\$?\d{1,3}(,\d{3})*\.\d{2}\)?-?(\s?(CR|DR))?$", RegexOptions.IgnoreCase)]
    private static partial Regex TextAmountPattern();

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
        string[] formats = ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy", "yyyyMMdd", "MMM d, yyyy"];
        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return true;
            }
        }

        return DateOnly.TryParse(text, CultureInfo.InvariantCulture, out date);
    }

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
}
