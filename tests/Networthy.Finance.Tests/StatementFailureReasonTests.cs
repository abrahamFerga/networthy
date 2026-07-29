using Networthy.Finance;

namespace Networthy.Finance.Tests;

/// <summary>
/// The failure reason a dead-end extraction reports must name the REAL fix — these four states
/// exist because the generic message once told a household to "enable an engine" they had
/// already enabled while their Tika container was simply not running.
/// </summary>
public sealed class StatementFailureReasonTests
{
    [Fact]
    public void TextReadButNoLines_BlamesTheLayout_NotOcr()
    {
        var reason = StatementParseJobHandler.ComposeFailureReason(
            new OcrDiagnostics { DocumentTextChars = 4321 });

        Assert.Contains("4,321 characters", reason);
        Assert.Contains("CSV or OFX/QFX", reason);
        Assert.DoesNotContain("OCR engine", reason);
    }

    [Fact]
    public void EngineConfiguredButUnreachable_NamesTheEngineAndSaysStartIt()
    {
        var reason = StatementParseJobHandler.ComposeFailureReason(new OcrDiagnostics
        {
            EngineConfigured = true,
            EngineFailure = "the household's self-hosted Tika OCR server (http://localhost:9998) " +
                            "could not be reached (Connection refused)",
        });

        Assert.Contains("http://localhost:9998", reason);
        Assert.Contains("running and reachable", reason);
        // The old lie: telling the user to ENABLE an engine that was already enabled.
        Assert.DoesNotContain("can enable an engine", reason);
    }

    [Fact]
    public void EngineRanButReadNothing_SuggestsASharperScan()
    {
        var reason = StatementParseJobHandler.ComposeFailureReason(new OcrDiagnostics
        {
            EngineConfigured = true,
            EngineReadNoText = true,
        });

        Assert.Contains("sharper scan", reason);
        Assert.DoesNotContain("make sure it is running", reason);
    }

    [Fact]
    public void NoEngineAtAll_KeepsTheEnableStory()
    {
        var reason = StatementParseJobHandler.ComposeFailureReason(new OcrDiagnostics());

        Assert.Contains("No extractor could read this file", reason);
        Assert.Contains("Admin → Integrations", reason);
    }

    [Fact]
    public void TextBeatsEngineFailure_WhenBothWereRecorded()
    {
        // OCR failed but a later source produced text: the parse gap is the story, not the outage.
        var reason = StatementParseJobHandler.ComposeFailureReason(new OcrDiagnostics
        {
            DocumentTextChars = 900,
            EngineConfigured = true,
            EngineFailure = "the household's self-hosted Tika OCR server (http://x) could not be reached (boom)",
        });

        Assert.Contains("no transaction lines were recognized", reason);
        Assert.DoesNotContain("could not be reached", reason);
    }
}
