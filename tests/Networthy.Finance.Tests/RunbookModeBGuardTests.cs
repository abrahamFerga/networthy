using System.Text.RegularExpressions;

namespace Networthy.Finance.Tests;

/// <summary>
/// <c>RUNBOOK.md</c> is the execution surface every agent and every new contributor starts from, so
/// a snippet in it that does not do what it says is not a documentation defect — it is a broken
/// tool. Worse, its failures are attributed to the product rather than to the page: all three
/// defects pinned below shipped in §2 Mode B at once, and each one presents to whoever hits it as
/// <i>"the chat is not working at all"</i>.
///
/// The committed request catalog is already tested for exactly this reason — see
/// <c>RequestCatalogTests</c> (issue #178), which argues that a catalog the next agent cannot
/// distinguish from a route regression must be "tested like code". These guards extend that same
/// argument from the endpoint list to the launch snippets.
///
/// They live in this project rather than in the integration suite on purpose: this one needs no
/// Docker, so it still runs under
/// <c>dotnet test --filter "FullyQualifiedName!~IntegrationTests"</c> — the lane a contributor
/// falls back to precisely when their environment is the thing misbehaving, which is when a wrong
/// runbook does the most damage.
/// </summary>
public sealed class RunbookModeBGuardTests
{
    private static readonly string Runbook =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RUNBOOK.md"));

    /// <summary>The fenced <c>powershell</c> blocks — the part a reader actually executes.</summary>
    private static readonly Regex PowerShellBlock = new(
        @"```powershell\r?\n(?<body>.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>`# …` to end of line — a PowerShell comment.</summary>
    private static readonly Regex Comment = new(@"#.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The fenced blocks with their comments stripped, so every assertion below is about what
    /// actually <i>executes</i>. This is not a convenience: the runbook has to be free to name
    /// each trap in order to warn against it — the fixed snippet says
    /// <c>$port = 8194  # …NOT 8094</c> and explains the <c>ForEach-Object</c> break in a comment
    /// directly above the loop — and a guard that read those warnings as violations would punish
    /// the page for documenting the very thing it fixed. Prose outside the fences is ignored for
    /// the same reason.
    /// <para>
    /// Strips <c>#</c> to end of line unconditionally, which would also blank a <c>#</c> inside a
    /// string literal. No snippet here has one, and the failure mode is a guard that under-reads
    /// rather than one that fires falsely.
    /// </para>
    /// </summary>
    private static List<string> Scripts()
    {
        var blocks = PowerShellBlock.Matches(Runbook)
            .Select(m => Comment.Replace(m.Groups["body"].Value, string.Empty))
            .ToList();

        // A regex that drifted from the file format would make every assertion below pass
        // vacuously — the one failure mode that would hide all the others.
        Assert.NotEmpty(blocks);
        return blocks;
    }

    /// <summary>The single block that launches the headless host — §2 Mode B.</summary>
    private static string LaunchScript() => Scripts().Single(s => s.Contains("--urls="));

    /// <summary>
    /// ContentRoot *is* the working directory, and it must resolve both
    /// <c>appsettings.Development.json</c> and <c>wwwroot/</c>. Only the first is copied to the
    /// build output, so a host launched from <c>bin</c> serves a flawless API — <c>/alive</c> 200,
    /// module loaded, AG-UI turns streaming correctly — and <b>404s on <c>/app</c></b>. No web UI
    /// at all, which reads as a dead product rather than as a bad launch line.
    /// </summary>
    [Fact]
    public void ModeB_LaunchesFromTheProjectFolder_SoTheUiIsActuallyServed()
    {
        var launch = LaunchScript();

        Assert.DoesNotContain("-WorkingDirectory $bin", launch);
        Assert.Contains("-WorkingDirectory $proj", launch);
    }

    /// <summary>
    /// <c>8094</c> is the Mode B default in several Plenipo products' runbooks at once
    /// (<c>auditworthy</c> and <c>casewell</c> both claim it), and a sibling host answers
    /// <c>/alive</c> with <c>Healthy</c> exactly as Networthy does. Only the module list tells them
    /// apart, so the readiness wait must gate on <c>finance</c> rather than on liveness:
    /// <c>POST /api/agui/finance</c> against the wrong product returns
    /// <c>RUN_ERROR "Unknown module"</c>, which is indistinguishable from a broken chat until you
    /// check which product answered.
    /// </summary>
    [Fact]
    public void ModeB_ProvesItReachedNetworthy_RatherThanASiblingPlenipoProduct()
    {
        var launch = LaunchScript();

        Assert.DoesNotContain("8094", launch);
        Assert.Contains("/api/platform/modules", launch);
        Assert.Contains("finance", launch);
    }

    /// <summary>
    /// <c>ForEach-Object</c> is a cmdlet, not a loop, so a <c>break</c> inside it has no loop to
    /// leave and Windows PowerShell terminates the <b>entire script</b> instead — silently, with
    /// exit code 0. The readiness wait that did this therefore never reached its own last line, so
    /// <c>Stop-Process</c> and <c>docker rm -f</c> never ran and <i>every single run leaked a live
    /// host holding the port</i>. That is the mechanism behind most "something is already
    /// listening" and stale-process reports here, including the sibling mix-up above.
    /// <para>
    /// Verified on PowerShell 5.1.26100:
    /// <c>1..3 | ForEach-Object { break }; 'after'</c> never prints <c>after</c>, while the same
    /// body under <c>foreach</c> does.
    /// </para>
    /// Line-scoped, because that is how the idiom is written — a pipeline on one line. A
    /// <c>break</c> several lines below a multi-line <c>ForEach-Object {</c> would slip past this,
    /// which is a limit worth knowing rather than a reason to skip the cheap check.
    /// </summary>
    [Fact]
    public void NoSnippet_BreaksInsideForEachObject_WhichWouldKillTheScriptAndLeakTheHost()
    {
        var offenders = Scripts()
            .SelectMany(s => s.Split('\n'))
            .Select(line => line.Trim())
            .Where(line => line.Contains("ForEach-Object") && Regex.IsMatch(line, @"\bbreak\b"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "`break` inside ForEach-Object terminates the whole script, so the cleanup line never "
            + "runs and the host leaks. Use a real `foreach` loop instead:\n  "
            + string.Join("\n  ", offenders));
    }
}
