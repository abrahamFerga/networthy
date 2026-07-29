using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plenipo.Application.Documents;
using Plenipo.Application.Security;
using Plenipo.Connectors.Sdk;

namespace Networthy.Finance;

/// <summary>
/// The OCR engine behind scanned-statement extraction, routing each call across the sources a
/// household can configure — no restart for any of them:
///
///   1. The self-hosted Tika connector (Integrations → "Self-hosted OCR (Apache Tika)"). First
///      on purpose: a household that bothered to run its own OCR server wants documents at home.
///   2. The Azure Document Intelligence connector (Integrations).
///   3. The deployment's <c>Ocr</c> configuration (env/user-secrets) — the operator fallback.
///   4. Honest null: scanned pages stay unread, exactly like an OCR-less deployment today.
///
/// A failing source logs and falls through to the next rather than sinking the import.
/// IConnectorSettings returns settings only while the tenant has the connector ENABLED (secrets
/// revealed from the platform vault), and both chat document reads and statement-parse jobs run
/// in a tenant scope — which is why this is scoped and per-household isolation just works.
/// Registered AFTER the platform (modules install second), so it wins DocumentReader's optional
/// IOcrEngine slot while the platform's boot-time engine remains untouched as the config seam.
/// </summary>
public sealed class OcrEngineRouter(
    IConnectorSettings connectors,
    IOptions<OcrOptions> deploymentOcr,
    IHttpClientFactory httpClients,
    OutboundUrlPolicy outboundUrls,
    OcrDiagnostics diagnostics,
    ILogger<OcrEngineRouter> logger) : IOcrEngine
{
    public const string TikaConnectorId = "ocr-apache-tika";
    public const string TikaBaseUrlKey = "BaseUrl";
    public const string TikaAuthorizationKey = "AuthorizationHeader";
    public const string TikaHttpClientName = "Networthy.Ocr.Tika";

    public const string AdiConnectorId = "ocr-azure-document-intelligence";
    public const string AdiEndpointKey = "Endpoint";
    public const string AdiApiKeyKey = "ApiKey";

    public string Name => "networthy-ocr-router";

    // Azure clients are thread-safe; cache across scopes per endpoint + key digest so a rotated
    // key gets a fresh client (the platform's TenantChatClientResolver pattern).
    private static readonly ConcurrentDictionary<string, DocumentIntelligenceClient> AdiClients = new();

    public async Task<string?> ExtractTextAsync(
        Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        // Buffer once: a failed source must not leave the next one a half-consumed stream.
        // Uploads are capped well below memory concern (Files:MaxUploadBytes).
        var document = await BinaryData.FromStreamAsync(content, cancellationToken);

        var viaTika = await TryTikaAsync(document, contentType, cancellationToken);
        if (viaTika is not null)
        {
            return NullIfBlank(viaTika);
        }

        var adi = await connectors.GetAsync(AdiConnectorId, cancellationToken);
        if (adi is not null
            && adi.TryGetValue(AdiEndpointKey, out var endpoint) && !string.IsNullOrWhiteSpace(endpoint)
            && adi.TryGetValue(AdiApiKeyKey, out var key) && !string.IsNullOrWhiteSpace(key))
        {
            diagnostics.EngineConfigured = true;
            var viaConnector = await TryAdiAsync(endpoint, key, document, contentType, "connector", cancellationToken);
            if (viaConnector is not null)
            {
                return NullIfBlank(viaConnector);
            }
        }

        var deployment = deploymentOcr.Value;
        if (deployment.IsAzureDocumentIntelligence)
        {
            diagnostics.EngineConfigured = true;
            var viaDeployment = await TryAdiAsync(
                deployment.Endpoint!, deployment.ApiKey!, document, contentType, "deployment", cancellationToken);
            if (viaDeployment is not null)
            {
                return NullIfBlank(viaDeployment);
            }
        }

        return null;
    }

    private async Task<string?> TryTikaAsync(BinaryData document, string contentType, CancellationToken cancellationToken)
    {
        var settings = await connectors.GetAsync(TikaConnectorId, cancellationToken);
        if (settings is null
            || !settings.TryGetValue(TikaBaseUrlKey, out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        diagnostics.EngineConfigured = true;
        try
        {
            // The deployment's egress policy applies to household-entered URLs (an in-network
            // Tika needs the personal-mode AllowPrivateNetworks/AllowHttp posture).
            await outboundUrls.RequireAllowedAsync(baseUrl, cancellationToken);

            // Tika's contract IS the OCR seam: PUT the bytes at /tika, read text back. The
            // "-full" image OCRs image-only PDF pages internally (X-Tika-PDFOcrStrategy: auto).
            using var request = new HttpRequestMessage(
                HttpMethod.Put, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "tika"));
            request.Content = new ByteArrayContent(document.ToArray());
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            request.Headers.TryAddWithoutValidation("Accept", "text/plain");
            request.Headers.TryAddWithoutValidation("X-Tika-PDFOcrStrategy", "auto");
            if (settings.TryGetValue(TikaAuthorizationKey, out var authorization) && !string.IsNullOrWhiteSpace(authorization))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            using var response = await httpClients.CreateClient(TikaHttpClientName)
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                diagnostics.EngineFailure ??=
                    $"the household's self-hosted Tika OCR server ({baseUrl}) answered HTTP {(int)response.StatusCode}";
                logger.LogWarning(
                    "The Tika OCR server answered {Status} for a {ContentType} document; trying the next OCR source",
                    (int)response.StatusCode, contentType);
                return null;
            }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or TaskCanceledException)
        {
            diagnostics.EngineFailure ??=
                $"the household's self-hosted Tika OCR server ({baseUrl}) could not be reached ({ex.Message})";
            logger.LogWarning(ex, "Self-hosted Tika OCR failed; trying the next OCR source");
            return null;
        }
    }

    private async Task<string?> TryAdiAsync(
        string endpoint, string key, BinaryData document, string contentType, string source, CancellationToken cancellationToken)
    {
        try
        {
            var client = AdiClients.GetOrAdd(
                $"{endpoint}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}",
                _ => new DocumentIntelligenceClient(new Uri(endpoint), new AzureKeyCredential(key)));
            var result = await client.AnalyzeDocumentAsync(
                WaitUntil.Completed, new AnalyzeDocumentOptions("prebuilt-read", document), cancellationToken);
            return result.Value.Content;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is RequestFailedException or UriFormatException)
        {
            diagnostics.EngineFailure ??=
                $"Azure Document Intelligence ({source}) could not process the document ({ex.Message})";
            logger.LogWarning(ex,
                "Azure Document Intelligence ({Source}) could not process a {ContentType} document",
                source, contentType);
            return null;
        }
    }

    /// <summary>An engine ANSWERED with blank text — a real read that found nothing, not an outage.</summary>
    private string? NullIfBlank(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            diagnostics.EngineReadNoText = true;
            return null;
        }

        return text;
    }
}
