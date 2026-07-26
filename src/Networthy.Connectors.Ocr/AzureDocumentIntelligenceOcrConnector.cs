using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Plenipo.Connectors.Sdk;

namespace Networthy.Connectors.Ocr;

/// <summary>
/// Azure Document Intelligence as a per-household connector: the managed-cloud OCR option,
/// configured under Integrations like any other integration instead of via deployment config.
/// BYO-resource — a household's scanned statements only ever touch the Azure account whose
/// endpoint and key the household enters here. The deployment-level <c>Ocr</c> configuration
/// remains as the operator-provided fallback beneath both connectors.
/// </summary>
public sealed class AzureDocumentIntelligenceOcrConnector : IConnector
{
    public ConnectorManifest Manifest { get; } = new()
    {
        Id = OcrEngineRouter.AdiConnectorId,
        DisplayName = "Azure Document Intelligence OCR",
        Description = "Read scanned (photographed) statements with your own Azure Document Intelligence " +
                      "resource (prebuilt-read). Digital PDFs, CSV and OFX never need OCR.",
        AuthMode = ConnectorAuthMode.Service,
        Icon = "cloud",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = OcrEngineRouter.AdiEndpointKey,
                Label = "Endpoint",
                Description = "The resource endpoint, e.g. https://your-resource.cognitiveservices.azure.com.",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = OcrEngineRouter.AdiApiKeyKey,
                Label = "API key",
                Description = "Stored protected; never shown again after saving.",
                Required = true,
                IsSecret = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Nothing to register: the finance module's OcrEngineRouter speaks to Azure directly
        // with the settings this manifest declares.
    }
}
