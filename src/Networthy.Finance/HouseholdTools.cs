using System.ComponentModel;
using Cortex.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Household sharing (SPEC must-have #6). A household IS a Cortex tenant, so membership,
/// invites, and role assignment are the platform's existing Admin → Users surface with the
/// product roles seeded in Networthy.Host (household-admin / household-member) — no duplicate
/// user machinery here. What the module owns is the per-member visibility scope on accounts:
/// an account is household-wide by default, or private to one member. Visibility changes are
/// record changes and approval-gated.
/// </summary>
public sealed class HouseholdTools(
    FinanceDbContext db,
    ICurrentUser currentUser)
{
    [Description("Make an account private to YOU (only you and household admins see it and its transactions) or shared with the whole household again. Side-effecting and requires approval.")]
    public async Task<string> SetAccountVisibility(
        [Description("The account name.")] string accountName,
        [Description("private (only me) or household (everyone).")] string visibility,
        CancellationToken cancellationToken = default)
    {
        var normalized = visibility.Trim().ToLowerInvariant();
        if (normalized is not ("private" or "household"))
        {
            return $"'{visibility}' is not a visibility. Use private or household.";
        }

        var account = await db.Accounts.FirstOrDefaultAsync(
            a => EF.Functions.ILike(a.Name, accountName.Trim()), cancellationToken);
        if (account is null || !account.IsVisibleTo(currentUser.UserId))
        {
            return $"No account named '{accountName}' exists (or it is private to another member). Use list_accounts.";
        }

        if (normalized == "private")
        {
            if (currentUser.UserId is null)
            {
                return "I can't determine who you are, so I can't make this account private to you.";
            }

            account.RestrictedToUserId = currentUser.UserId;
        }
        else
        {
            account.RestrictedToUserId = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        return normalized == "private"
            ? $"'{account.Name}' is now private to you — other members won't see it or its transactions."
            : $"'{account.Name}' is now shared with the whole household.";
    }
}
