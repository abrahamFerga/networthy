using System.Text.RegularExpressions;

namespace Networthy.Host;

/// <summary>
/// Decides which ids from OpenAI's <c>/v1/models</c> catalog belong in the admin Model dropdown.
/// The raw catalog lists everything the account can touch — image generation, TTS, transcription,
/// embeddings, moderation, video — but the assistant only ever calls the Chat Completions API, so
/// offering <c>dall-e-3</c> or <c>whisper-1</c> is an invitation to break chat with one click.
/// Pattern rules rather than a hardcoded list: the catalog is fetched live, and new chat models
/// should appear without a redeploy while new modalities stay out.
/// </summary>
public static partial class OpenAiChatModels
{
    /// <summary>
    /// Substrings that mark a model as speaking some other API than Chat Completions: other
    /// modalities (image, audio, video, embeddings, moderation), Responses-API-only families
    /// (deep-research matches via "search", codex, computer-use), and the legacy completions
    /// models (instruct, babbage, davinci). "-16k" drops the deprecated long-context alias of
    /// gpt-3.5-turbo, whose base id already stays.
    /// </summary>
    private static readonly string[] NonChatMarkers =
    [
        "image", "dall-e", "sora", "audio", "realtime", "tts", "transcribe", "whisper",
        "embed", "moderation", "search", "instruct", "davinci", "babbage", "codex",
        "computer-use", "-16k",
    ];

    /// <summary>True for ids the chat pipeline can actually use.</summary>
    public static bool IsChatCompletionModel(string id)
    {
        var m = id.Trim().ToLowerInvariant();

        // The families that speak Chat Completions: gpt-*, the chatgpt-* aliases, and the
        // o-series reasoning models. Everything else in the catalog is a different product.
        if (!m.StartsWith("gpt-", StringComparison.Ordinal)
            && !m.StartsWith("chatgpt-", StringComparison.Ordinal)
            && !OSeries().IsMatch(m))
        {
            return false;
        }

        if (NonChatMarkers.Any(m.Contains))
        {
            return false;
        }

        // The "-pro" tiers (o1-pro, o3-pro, gpt-5-pro…) are Responses-API-only.
        if (m.EndsWith("-pro", StringComparison.Ordinal) || m.Contains("-pro-", StringComparison.Ordinal))
        {
            return false;
        }

        // Dated snapshots (gpt-4.1-2025-04-14, gpt-4-0613) duplicate an alias that also appears
        // in the catalog; keeping only the alias is what turns 123 rows into a usable handful.
        return !DatedSnapshot().IsMatch(m);
    }

    /// <summary>Filters a raw catalog, preserving the provider's ordering.</summary>
    public static IReadOnlyList<string> FilterCatalog(IEnumerable<string> ids)
        => ids.Where(IsChatCompletionModel).ToArray();

    [GeneratedRegex("^o[0-9](-|$)")]
    private static partial Regex OSeries();

    [GeneratedRegex("-[0-9]{4}(-[0-9]{2}-[0-9]{2})?$")]
    private static partial Regex DatedSnapshot();
}
