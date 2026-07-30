using Plenipo.Modules.Sdk;
using Xunit;

namespace Networthy.Finance.Tests;

/// <summary>
/// Guards for the shims this product carries because the platform cannot yet express something.
/// These tests assert the platform is STILL missing the capability — so they go RED on the upgrade
/// PR that fixes it, and that failure is the signal to delete the shim. A TODO tag on its own is
/// passive: nobody greps for it, and a shim with no failing test is technical debt with no creditor.
///
/// When one of these fails, do not "fix" the test. Delete the shim it names, then delete the test.
/// </summary>
public sealed class PlatformShimGuardTests
{
    /// <summary>
    /// Pairs with <c>TODO(plenipo#66)</c> in <c>FinanceModule</c>'s review tab (the "Posts into"
    /// column is plain text because a column cannot carry a link) AND with
    /// <c>TODO(plenipo#65)</c> (the shouted detail-section headings, because a section cannot
    /// declare a tone).
    ///
    /// Both requests land in the same platform release, and only the column one leaves a
    /// server-side surface a test can probe — section tone is carried in an anonymous JSON object
    /// the platform never types, so there is nothing to reflect over. This single guard therefore
    /// covers both shims, which is why it names both issues.
    /// <para>
    /// https://github.com/abrahamFerga/Plenipo/issues/65 —
    /// https://github.com/abrahamFerga/Plenipo/issues/66
    /// </para>
    /// </summary>
    [Fact]
    public void Shims_StillNeeded_PlatformTabColumnCannotCarryALink()
    {
        // Reflection rather than a compile-time reference on purpose: referencing a property that
        // does not exist yet would not compile, and this has to keep building against the CURRENT
        // pinned platform while still noticing the moment that changes.
        var link = typeof(TabColumn).GetProperty("LinkTemplate");

        Assert.Null(link);
    }
}
