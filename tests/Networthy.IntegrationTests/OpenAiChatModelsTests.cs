using System.Text.Json.Nodes;
using Networthy.Host;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The chat-model filter behind the admin Model dropdown (PlenipoAiShims shim 2). The fixture
/// list is the real /v1/models surface an OpenAI account exposes — the dropdown should end up
/// with the handful of chat-completion models the assistant can actually run on.
/// </summary>
public sealed class OpenAiChatModelsTests
{
    [Theory]
    // The chat-completion families stay — aliases, minis, and reasoning models.
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("gpt-4.1", true)]
    [InlineData("gpt-4.1-mini", true)]
    [InlineData("gpt-4.1-nano", true)]
    [InlineData("gpt-4-turbo", true)]
    [InlineData("gpt-4", true)]
    [InlineData("gpt-3.5-turbo", true)]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5-mini", true)]
    [InlineData("chatgpt-4o-latest", true)]
    [InlineData("gpt-5-chat-latest", true)]
    [InlineData("o1", true)]
    [InlineData("o1-mini", true)]
    [InlineData("o3", true)]
    [InlineData("o3-mini", true)]
    [InlineData("o4-mini", true)]
    // Image, video, and audio models would break chat with one click.
    [InlineData("chatgpt-image-latest", false)]
    [InlineData("gpt-image-1", false)]
    [InlineData("dall-e-3", false)]
    [InlineData("sora-2", false)]
    [InlineData("gpt-4o-realtime-preview", false)]
    [InlineData("gpt-4o-audio-preview", false)]
    [InlineData("gpt-4o-mini-tts", false)]
    [InlineData("gpt-4o-transcribe", false)]
    [InlineData("tts-1-hd", false)]
    [InlineData("whisper-1", false)]
    // Non-chat text surfaces: embeddings, moderation, legacy completions.
    [InlineData("text-embedding-3-large", false)]
    [InlineData("omni-moderation-latest", false)]
    [InlineData("gpt-3.5-turbo-instruct", false)]
    [InlineData("babbage-002", false)]
    [InlineData("davinci-002", false)]
    // Responses-API-only tiers and agents can't serve the Chat Completions pipeline.
    [InlineData("o1-pro", false)]
    [InlineData("o3-pro", false)]
    [InlineData("gpt-5-pro", false)]
    [InlineData("o3-deep-research", false)]
    [InlineData("codex-mini-latest", false)]
    [InlineData("computer-use-preview", false)]
    // Dated snapshots duplicate their alias; the alias row is the one that stays.
    [InlineData("gpt-4.1-2025-04-14", false)]
    [InlineData("gpt-4-turbo-2024-04-09", false)]
    [InlineData("gpt-4-0613", false)]
    [InlineData("gpt-3.5-turbo-0125", false)]
    [InlineData("gpt-3.5-turbo-16k", false)]
    public void IsChatCompletionModel_SplitsTheCatalog(string id, bool expected)
        => Assert.Equal(expected, OpenAiChatModels.IsChatCompletionModel(id));

    [Fact]
    public void FilterModelsProperty_RewritesOnlyTheModelsArray()
    {
        var body = JsonNode.Parse(
            """{"models":["babbage-002","gpt-4o","tts-1","gpt-4o-mini"],"supportsDiscovery":true,"message":null}""")!.AsObject();

        Assert.True(PlenipoAiShims.FilterModelsProperty(body));

        Assert.Equal(["gpt-4o", "gpt-4o-mini"], body["models"]!.AsArray().Select(m => (string)m!).ToArray());
        Assert.True((bool)body["supportsDiscovery"]!);
    }

    [Fact]
    public void FilterModelsProperty_LeavesShapesWithoutAModelsArrayAlone()
    {
        var body = JsonNode.Parse("""{"message":"nope"}""")!.AsObject();
        Assert.False(PlenipoAiShims.FilterModelsProperty(body));
    }
}
