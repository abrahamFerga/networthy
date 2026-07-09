using System.Net.Http.Json;
using System.Text.Json;

namespace Networthy.Connectors.Plaid;

/// <summary>The tenant's Plaid connection, resolved from protected connector settings per call.</summary>
public sealed record PlaidConnection(string ClientId, string ClientSecret, string Environment, string AccessToken)
{
    /// <summary>Plaid's per-environment API host.</summary>
    public string BaseUrl => Environment.Trim().ToLowerInvariant() switch
    {
        "production" => "https://production.plaid.com",
        "development" => "https://development.plaid.com",
        _ => "https://sandbox.plaid.com",
    };
}

/// <summary>A linked account as Plaid reports it.</summary>
public sealed record PlaidAccount(string AccountId, string Name, string? Subtype, decimal? CurrentBalance, string? Currency);

/// <summary>A transaction as Plaid reports it. Plaid convention: positive = money LEAVING the account.</summary>
public sealed record PlaidTransaction(string TransactionId, string AccountId, DateOnly Date, decimal Amount, string Name, string? Currency);

/// <summary>
/// The slice of the Plaid API the connector needs — a seam so keyless tests fake the bank while
/// production speaks HTTPS to Plaid (no vendor SDK; two POST endpoints).
/// </summary>
public interface IPlaidClient
{
    public Task<IReadOnlyList<PlaidAccount>> GetAccountsAsync(PlaidConnection connection, CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<PlaidTransaction>> GetTransactionsAsync(
        PlaidConnection connection, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Raw-HTTP Plaid implementation: POST JSON with client_id/secret/access_token in the body,
/// per Plaid's API shape (/accounts/get, /transactions/get).</summary>
public sealed class PlaidApiClient(IHttpClientFactory httpClientFactory) : IPlaidClient
{
    public const string HttpClientName = "plaid";

    public async Task<IReadOnlyList<PlaidAccount>> GetAccountsAsync(
        PlaidConnection connection, CancellationToken cancellationToken = default)
    {
        using var json = await PostAsync(connection, "/accounts/get", new
        {
            client_id = connection.ClientId,
            secret = connection.ClientSecret,
            access_token = connection.AccessToken,
        }, cancellationToken);

        var result = new List<PlaidAccount>();
        foreach (var account in json.RootElement.GetProperty("accounts").EnumerateArray())
        {
            var balances = account.TryGetProperty("balances", out var b) ? b : default;
            result.Add(new PlaidAccount(
                account.GetProperty("account_id").GetString()!,
                account.GetProperty("name").GetString() ?? "(unnamed)",
                account.TryGetProperty("subtype", out var st) ? st.GetString() : null,
                balances.ValueKind == JsonValueKind.Object && balances.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Number
                    ? cur.GetDecimal() : null,
                balances.ValueKind == JsonValueKind.Object && balances.TryGetProperty("iso_currency_code", out var cc)
                    ? cc.GetString() : null));
        }

        return result;
    }

    public async Task<IReadOnlyList<PlaidTransaction>> GetTransactionsAsync(
        PlaidConnection connection, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        using var json = await PostAsync(connection, "/transactions/get", new
        {
            client_id = connection.ClientId,
            secret = connection.ClientSecret,
            access_token = connection.AccessToken,
            start_date = from.ToString("yyyy-MM-dd"),
            end_date = to.ToString("yyyy-MM-dd"),
            options = new { count = 500 },
        }, cancellationToken);

        var result = new List<PlaidTransaction>();
        foreach (var txn in json.RootElement.GetProperty("transactions").EnumerateArray())
        {
            result.Add(new PlaidTransaction(
                txn.GetProperty("transaction_id").GetString()!,
                txn.GetProperty("account_id").GetString()!,
                DateOnly.Parse(txn.GetProperty("date").GetString()!),
                txn.GetProperty("amount").GetDecimal(),
                txn.GetProperty("name").GetString() ?? "(unnamed)",
                txn.TryGetProperty("iso_currency_code", out var cc) ? cc.GetString() : null));
        }

        return result;
    }

    private async Task<JsonDocument> PostAsync(
        PlaidConnection connection, string path, object body, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        var response = await http.PostAsJsonAsync($"{connection.BaseUrl}{path}", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}

/// <summary>Pure Plaid → Networthy mapping rules, unit-tested without HTTP.</summary>
public static class PlaidMapping
{
    /// <summary>Plaid's sign convention is the inverse of ours: positive = outflow = expense.</summary>
    public static (decimal Amount, string Direction) ToNetworthy(decimal plaidAmount) =>
        plaidAmount >= 0 ? (plaidAmount, "expense") : (-plaidAmount, "income");

    /// <summary>Plaid subtypes → the four account types the finance module speaks.</summary>
    public static string ToAccountType(string? plaidSubtype) => plaidSubtype?.ToLowerInvariant() switch
    {
        "checking" => "checking",
        "savings" or "money market" or "cd" => "savings",
        "credit card" or "credit" => "credit",
        _ => "checking",
    };

    /// <summary>The dedupe identity for a synced transaction (Plaid re-reports history freely).</summary>
    public static string DedupeKey(DateOnly date, decimal amount, string description) =>
        $"{date:yyyyMMdd}|{amount:0.00}|{description.Trim().ToUpperInvariant()}";
}
