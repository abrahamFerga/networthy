using System.ComponentModel;
using System.Text;
using Cortex.Application.Approvals;
using Cortex.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The differentiator, made visible (SPEC differentiator #2): the approval gate and the
/// append-only audit log exist at the platform layer for every write already — these tools
/// surface them in the household's own vocabulary. list_pending_approvals shows what the AI
/// wants to do and is waiting on; get_activity_log shows what actually changed in the books,
/// who did it, and where it came from. The full tool-call audit (every invocation, arguments,
/// approver) lives in the admin console's Audit page, which the platform ships.
/// </summary>
public sealed class ApprovalSurfaceTools(
    FinanceDbContext db,
    IApprovalStore approvals,
    ICurrentUser currentUser)
{
    [Description("Everything the AI is waiting on: pending approval requests for this household, newest first.")]
    public async Task<string> ListPendingApprovals(CancellationToken cancellationToken = default)
    {
        var pending = await approvals.ListPendingAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return "Nothing is waiting for approval.";
        }

        var sb = new StringBuilder($"{pending.Count} pending approval(s):\n");
        foreach (var p in pending.Take(20))
        {
            sb.AppendLine($"- {p.ToolName} — requested by {p.UserDisplay ?? "someone"} " +
                          $"({p.CreatedAt:yyyy-MM-dd HH:mm}Z). Approve or reject it from the conversation or the Approvals inbox.");
        }

        return sb.ToString();
    }

    [Description("The household's recent activity: what changed in the books (accounts, transactions, budgets, imports), when, by whom, and from which source. The complete tool-call audit lives in the admin console's Audit page.")]
    public async Task<string> GetActivityLog(
        [Description("How many days back (default 7, max 90).")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90));

        var accounts = await db.Accounts.ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);
        var visible = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .Select(a => a.Id)
            .ToHashSet();

        var events = new List<(DateTimeOffset At, string Line)>();

        foreach (var t in (await db.Transactions.Where(t => t.CreatedAt >= since).ToListAsync(cancellationToken))
                 .Where(t => visible.Contains(t.AccountId)))
        {
            events.Add((t.CreatedAt,
                $"{t.CreatedAt:yyyy-MM-dd HH:mm}Z · transaction ({t.Source}): " +
                $"{(t.Direction == "income" ? "+" : "-")}{t.Amount:N2} {t.CurrencyCode} · {t.Description} " +
                $"on {accounts.GetValueOrDefault(t.AccountId, "?")}"));
        }

        foreach (var b in await db.ImportBatches.Where(b => b.CreatedAt >= since).ToListAsync(cancellationToken))
        {
            events.Add((b.CreatedAt,
                $"{b.CreatedAt:yyyy-MM-dd HH:mm}Z · statement import '{b.FileName}' — {b.Status}" +
                $"{(b.ReviewedAt is { } r ? $", reviewed {r:yyyy-MM-dd HH:mm}Z" : "")}"));
        }

        foreach (var budget in await db.Budgets.Where(b => b.CreatedAt >= since).ToListAsync(cancellationToken))
        {
            events.Add((budget.CreatedAt,
                $"{budget.CreatedAt:yyyy-MM-dd HH:mm}Z · budget set: {budget.TargetAmount:N2} {budget.CurrencyCode} for {budget.PeriodMonth:yyyy-MM}"));
        }

        foreach (var a in (await db.Accounts.Where(a => a.CreatedAt >= since).ToListAsync(cancellationToken))
                 .Where(a => visible.Contains(a.Id)))
        {
            events.Add((a.CreatedAt, $"{a.CreatedAt:yyyy-MM-dd HH:mm}Z · account created: {a.Name} [{a.Type}]"));
        }

        if (events.Count == 0)
        {
            return $"No activity in the last {days} day(s).";
        }

        var sb = new StringBuilder($"Activity, last {days} day(s) (newest first):\n");
        foreach (var (_, line) in events.OrderByDescending(e => e.At).Take(40))
        {
            sb.AppendLine($"- {line}");
        }

        sb.Append("Every gated write above passed a human approval first; the full tool-call audit " +
                  "(arguments, approver, timestamps) is in the admin console under Audit.");
        return sb.ToString();
    }
}
