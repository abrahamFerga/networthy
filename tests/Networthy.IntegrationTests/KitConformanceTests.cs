using Plenipo.Testing.Conformance;
using Plenipo.Testing.Evals;

namespace Networthy.IntegrationTests;

// The platform publishes its own tests; this product executes them against its own host on every
// pull request. Everything below is one line because everything below is the PLATFORM's assertion,
// not Networthy's — the security spine (S01–S15), manifest integrity, tenant isolation, the agent
// guardrails (R1–R4) and the golden-eval runner over this repo's own Evals/cases. They upgrade with
// PlenipoVersion, so a future release's new invariant runs here the moment the package moves.
//
// A failure in one of these is a finding about Networthy or about the platform — never a reason to
// override the pack.

[Collection("api")]
public sealed class SpineConformance(IntegrationFixture fixture) : PlenipoSpineConformance<Program>(fixture);

[Collection("api")]
public sealed class ManifestConformance(IntegrationFixture fixture) : PlenipoManifestConformance<Program>(fixture);

[Collection("api")]
public sealed class TenancyConformance(IntegrationFixture fixture) : PlenipoTenancyConformance<Program>(fixture);

[Collection("api")]
public sealed class RedTeamConformance(IntegrationFixture fixture) : PlenipoRedTeamConformance<Program>(fixture);

[Collection("api")]
public sealed class GoldenConversationEvals(IntegrationFixture fixture) : PlenipoGoldenEvals<Program>(fixture);
