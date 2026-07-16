# The hosted service (how the paid lane works)

Networthy is open source first — [self-hosting](SELF_HOSTING.md) is free forever and feature
complete. The hosted service is for households that don't want to run a server: **a small
monthly fee that covers exactly two things — the shared infrastructure and the AI tokens the
assistant consumes.** No feature gates, no open-core split; the image the service runs is the
public one.

## Plans

The host already declares these (see `src/Networthy.Host/Program.cs` — the plan, not checkout
metadata, is authoritative for what a purchase grants):

| Plan | Seats | AI allowance / month | Runs on |
|---|---|---|---|
| `solo` | 1 | 200k tokens (platform-managed key) | Shared infrastructure, own tenant |
| `household` | 6 | 500k tokens (platform-managed key) | Shared infrastructure, own tenant |
| `dedicated` | unlimited | metered | Their own cloud environment (Terraform-provisioned) |

Notes on the model:

- **Every paying customer is a full tenant** — the platform's isolation (RBAC, query filters,
  per-tenant audit) is the product's isolation. Solo is not a degraded mode; it's a 1-seat tenant.
- **AI cost is the honest meter.** Platform-managed keys meter per-tenant token usage (already
  tracked for every chat turn) and push it to the billing meter. A tenant that connects **its
  own AI key pays no token fee** — the platform never falls back to the operator's key, so the
  boundary is structural, not policy.
- **Token budgets are enforced**, not aspirational: when a tenant's monthly budget is spent,
  the assistant says so instead of silently billing overage.

## What turns the machinery on (operator checklist)

Billing is **off by default** — a fresh deployment has no webhook surface at all. The Plenipo
commerce layer activates from configuration (secrets via user-secrets locally, Key Vault /
environment in production — never appsettings):

```text
Commerce:Enabled                    true
Commerce:WebhookSecret              whsec_…            (SECRET — Stripe webhook signing)
Commerce:StripeApiKey               sk_live_…          (SECRET — checkout + usage meter)
Commerce:Prices:networthy:solo      price_…            (Stripe Price ids — not secret)
Commerce:Prices:networthy:household price_…
Commerce:Prices:networthy:dedicated price_…
Commerce:CheckoutSuccessUrl         https://…/welcome
Commerce:CheckoutCancelUrl          https://…/pricing
Commerce:Dedicated:Owner            abrahamFerga       (dedicated tier only)
Commerce:Dedicated:Repo             Plenipo
Commerce:Dedicated:Workflow         deploy-customer.yml
Commerce:Dedicated:Token            (SECRET — fine-grained PAT, actions:write)
```

The flow from there is platform machinery, already integration-tested in Plenipo: Stripe
Checkout (metadata carries product/plan/org/admin) → signed webhook → durable event inbox →
provisioning worker → tenant + admin + seat limit + AI budget live in one transaction; the
`dedicated` plan dispatches the Terraform `deploy-customer` workflow instead. Suspension
(`past_due` → tenant `IsActive` off), cancellation grace, and deprovisioning are the same
state machine.

## Pricing stance

Price the fee to cover cost, not to gate features: infrastructure share + the plan's token
allowance at provider list price + a modest margin for operations. Publish the numbers on the
pricing page and say what they cover — the audience this product courts (people who compare us
to Firefly III and Actual Budget) rewards that honesty.
