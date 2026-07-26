using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Plenipo.Application.Ai;

namespace Networthy.Host;

/// <summary>
/// Host-side shims over three AI seams in Plenipo 0.1.0-alpha.25. The admin SPA and the platform
/// endpoints ship frozen in the packages, so until the fixes land upstream
/// (github.com/abrahamFerga/Plenipo) the host straightens them out at the HTTP boundary:
///
/// 1. PUT /api/admin/ai-settings — the SPA's Model dropdown offers "Default: gpt-4o-mini" (an
///    empty model), but the platform validator refuses an empty model for the OpenAI provider,
///    so picking the default makes the whole save fail. The shim writes the deployment-default
///    model into the request, which is exactly what EffectiveAiSettings.Merge would resolve.
/// 2. POST /api/admin/ai-models — OpenAI's raw catalog (123 models: image, TTS, embeddings…)
///    is filtered down to chat-completion models; see OpenAiChatModels.
/// 3. GET /api/platform/info — the platform computes demoMode/chatEnabled from the deployment
///    defaults only, so the "Demo mode" banner keeps showing after a tenant configures a real
///    provider. The shim re-answers from the tenant-effective settings.
///
/// Each shim is deletion-ready: when a platform release fixes the seam, drop the branch here.
/// </summary>
public static class PlenipoAiShims
{
    public static void UsePlenipoAiShims(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            if (HttpMethods.IsPut(context.Request.Method) && path == "/api/admin/ai-settings")
            {
                await FillDefaultModelAsync(context);
                await next();
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && path == "/api/admin/ai-models")
            {
                // The filter is OpenAI-only: Anthropic's catalog is already all-chat, and an
                // Ollama install lists local models these patterns must not touch.
                var provider = await PeekProviderAsync(context);
                if (string.Equals(provider, "OpenAI", StringComparison.Ordinal))
                {
                    await RewriteOkJsonResponseAsync(
                        context, next, obj => ValueTask.FromResult(FilterModelsProperty(obj)));
                    return;
                }
                await next();
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method) && path == "/api/platform/info")
            {
                await RewriteOkJsonResponseAsync(context, next, async obj =>
                {
                    EffectiveAiSettings effective;
                    try
                    {
                        effective = await context.RequestServices
                            .GetRequiredService<ITenantAiSettings>()
                            .ResolveAsync(context.RequestAborted);
                    }
                    catch (Exception)
                    {
                        // No tenant context (or the resolver failed) — the platform's
                        // deployment-level answer is the honest one to keep.
                        return false;
                    }
                    obj["chatEnabled"] = effective.IsEnabled;
                    obj["demoMode"] = string.Equals(effective.Provider, "Mock", StringComparison.OrdinalIgnoreCase);
                    return true;
                });
                return;
            }

            await next();
        });
    }

    /// <summary>
    /// Shim 1: when the tenant picks OpenAI and leaves Model on "Default", store the deployment
    /// default explicitly instead of letting the platform validator reject the save. Only OpenAI:
    /// the deployment default model is an OpenAI id, so injecting it under Anthropic/Azure/Ollama
    /// would silently configure a model those providers don't have.
    /// </summary>
    private static async Task FillDefaultModelAsync(HttpContext context)
    {
        using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        var raw = buffered.ToArray();
        var final = raw;

        try
        {
            if (JsonNode.Parse(raw) is JsonObject body
                && string.Equals((string?)body["provider"], "OpenAI", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace((string?)body["model"]))
            {
                var defaults = context.RequestServices.GetRequiredService<IOptions<AiOptions>>().Value;
                body["model"] = defaults.Model;
                final = Encoding.UTF8.GetBytes(body.ToJsonString());
            }
        }
        catch (JsonException)
        {
            // Malformed body: hand the original bytes to the platform endpoint and let its own
            // model binding produce the 400.
        }

        context.Request.Body = new MemoryStream(final);
        context.Request.ContentLength = final.Length;
    }

    /// <summary>
    /// Shim 2's body transform: replaces the "models" array of an ai-models response with its
    /// chat-completion subset. Public so tests can drive it without an OpenAI call.
    /// </summary>
    public static bool FilterModelsProperty(JsonObject obj)
    {
        if (obj["models"] is not JsonArray models)
        {
            return false;
        }
        var kept = OpenAiChatModels.FilterCatalog(
            models.Select(m => (string?)m).Where(m => !string.IsNullOrWhiteSpace(m))!);
        obj["models"] = new JsonArray([.. kept.Select(m => JsonValue.Create(m))]);
        return true;
    }

    /// <summary>Reads the request's "provider" property, leaving the body rewound for the endpoint.</summary>
    private static async Task<string?> PeekProviderAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        string? provider = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("provider", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                provider = value.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON — the endpoint will reject it; nothing for the shim to do.
        }
        context.Request.Body.Position = 0;
        return provider;
    }

    /// <summary>
    /// Runs the pipeline with the response buffered; on a 200 JSON object, lets <paramref name="mutate"/>
    /// rewrite it (return false to keep the original). Anything else — errors, other content types —
    /// passes through byte-for-byte, so the platform's status codes and messages survive.
    /// </summary>
    private static async Task RewriteOkJsonResponseAsync(
        HttpContext context, Func<Task> next, Func<JsonObject, ValueTask<bool>> mutate)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next();

            if (context.Response.StatusCode == StatusCodes.Status200OK
                && context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                buffer.Position = 0;
                JsonNode? node = null;
                try
                {
                    node = await JsonNode.ParseAsync(buffer, cancellationToken: context.RequestAborted);
                }
                catch (JsonException)
                {
                    // Unparseable 200 — fall through and relay it unchanged.
                }

                if (node is JsonObject obj && await mutate(obj))
                {
                    var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
                    context.Response.ContentLength = bytes.Length;
                    await original.WriteAsync(bytes, context.RequestAborted);
                    return;
                }
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = original;
        }
    }
}
