using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Plenipo.Application.Security;
using Plenipo.Connectors.Sdk;

namespace Networthy.Connectors.Ocr;

/// <summary>
/// Self-hosted OCR via Apache Tika (Apache-2.0; the official <c>apache/tika:&lt;ver&gt;-full</c>
/// image bundles Tesseract): scanned statements go PDF-in/text-out to a server the household or
/// operator runs — documents never leave their infrastructure. Tika is the open-source default
/// because its contract matches the OCR seam exactly and it rasterizes image-only PDF pages
/// internally; engines with image-only APIs (raw tesseract-server, PaddleOCR) would additionally
/// need a PDF rasterizer the platform doesn't ship.
/// </summary>
public sealed class TikaOcrConnector : IConnector
{
    public ConnectorManifest Manifest { get; } = new()
    {
        Id = OcrEngineRouter.TikaConnectorId,
        DisplayName = "Self-hosted OCR (Apache Tika)",
        Description = "Read scanned (photographed) statements with your own Apache Tika server — " +
                      "e.g. `docker compose --profile ocr up -d`, then point this at http://tika:9998. " +
                      "Outranks Azure Document Intelligence when both are enabled, so documents stay home.",
        AuthMode = ConnectorAuthMode.Service,
        Icon = "server",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = OcrEngineRouter.TikaBaseUrlKey,
                Label = "Server URL",
                Description = "The Tika server base URL: http://tika:9998 (compose profile), " +
                              "http://localhost:9998 (the Aspire dashboard's tika resource), or your own https host.",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = OcrEngineRouter.TikaAuthorizationKey,
                Label = "Authorization header",
                Description = "Optional — sent verbatim as the Authorization header (e.g. \"Bearer …\") " +
                              "for Tika behind an authenticating reverse proxy. Stored protected.",
                IsSecret = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // OCR of a big scanned statement is slow — give Tika room well past HttpClient's 100 s
        // default. The platform's outbound policy handler enforces the egress rules at connect
        // time (private/loopback destinations need Security:OutboundUrls:AllowPrivateNetworks,
        // which the personal-mode/dev posture enables).
        services.AddHttpClient(OcrEngineRouter.TikaHttpClientName, c => c.Timeout = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<OutboundUrlPolicy>().CreateHttpMessageHandler());
    }
}
