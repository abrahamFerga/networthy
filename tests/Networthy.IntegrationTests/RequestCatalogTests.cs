using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The committed request catalog is a rung of the verification surface, not documentation:
/// RUNBOOK.md §4 calls <c>networthy.http</c> "the runnable list of every endpoint", and the next
/// agent reaching for it to exercise something has no way to tell a route regression from a
/// catalog that was always wrong. So the catalog is tested like code (issue #178).
///
/// Every request is sent <b>unauthenticated</b>, and that is the whole trick. ASP.NET Core selects
/// the endpoint during routing and answers 405 on a method mismatch <i>before</i> the authorization
/// middleware runs — so a wrong verb still surfaces, while no authorized handler ever executes and
/// nothing in the shared fixture's database is mutated by a catalog full of POSTs and DELETEs.
/// </summary>
[Collection("api")]
public sealed class RequestCatalogTests(IntegrationFixture fixture)
{
    /// <summary>`GET {{baseUrl}}/api/…` — the request line of a .http entry.</summary>
    private static readonly Regex RequestLine = new(
        @"^(?<method>GET|POST|PUT|PATCH|DELETE)\s+\{\{baseUrl\}\}(?<path>\S*)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>`@accountId = 0000…` — the variable definitions the requests interpolate.</summary>
    private static readonly Regex VariableLine = new(
        @"^@(?<name>\w+)\s*=\s*(?<value>.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public async Task EveryRequestInTheCommittedCatalog_IsRoutable()
    {
        var catalog = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "networthy.http"));

        var variables = VariableLine.Matches(catalog)
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value);

        var requests = RequestLine.Matches(catalog)
            .Select(m => (
                Method: m.Groups["method"].Value,
                Path: Interpolate(m.Groups["path"].Value, variables),
                Line: catalog.Take(m.Index).Count(c => c == '\n') + 1))
            .ToList();

        // A catalog this test found empty would pass vacuously — the regex drifting from the file
        // format is the failure mode that would hide every real mismatch.
        Assert.True(requests.Count >= 70, $"only parsed {requests.Count} requests; the .http format changed");

        // No dev-auth headers: routing decides 405, authorization decides 401/403, and only the
        // first is under test. Anything that answers 401 is routable, which is the whole claim.
        using var anonymous = fixture.Factory.CreateClient();

        var unroutable = new List<string>();
        foreach (var (method, path, line) in requests)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await anonymous.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.MethodNotAllowed)
            {
                var allowed = response.Content.Headers.Allow.Count > 0
                    ? string.Join(", ", response.Content.Headers.Allow)
                    : "(none advertised)";
                unroutable.Add($"networthy.http:{line}  {method} {path} → 405, route accepts: {allowed}");
            }
        }

        Assert.True(unroutable.Count == 0,
            "the committed catalog documents requests the API will not accept:\n  "
            + string.Join("\n  ", unroutable));
    }

    private static string Interpolate(string path, IReadOnlyDictionary<string, string> variables) =>
        Regex.Replace(path, @"\{\{(?<name>\w+)\}\}",
            m => variables.TryGetValue(m.Groups["name"].Value, out var value) ? value : m.Value);
}
