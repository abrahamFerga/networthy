using Plenipo.Testing;

namespace Networthy.IntegrationTests;

/// <summary>
/// The real host on a throwaway Postgres: platform + finance migrations run, the dev tenant and
/// category taxonomy seed, the job processor and hosted services start. Everything is real
/// except the AI provider (Mock) — the same keyless posture the Plenipo platform's own suite uses.
/// <para>
/// The container, the <see cref="PlenipoHostFixture{TProgram}.Factory"/>, the dev-auth clients and
/// the tenant helpers all come from the platform's <c>Plenipo.Testing</c> conformance kit, which
/// ships at <c>PlenipoVersion</c> — so upgrading the platform upgrades the harness and the
/// invariants together, instead of leaving this product on a copy that silently drifts. All this
/// class owns is the <see cref="Contract"/>: what the kit needs to know about Networthy to run the
/// platform's invariants against THIS host.
/// </para>
/// <para>
/// The class name is deliberately unchanged: every suite in this project takes an
/// <c>IntegrationFixture</c> by constructor, and the value of adopting the kit is in what it now
/// derives from, not in a rename.
/// </para>
/// </summary>
public sealed class IntegrationFixture : PlenipoHostFixture<Program>
{
    /// <summary>
    /// Networthy's conformance subject, read from the finance manifest and Program.cs's role model
    /// — never invented:
    /// <list type="bullet">
    ///   <item><c>summarize_spending</c> is a read: no <c>RequiresApproval</c>, and every household
    ///     role holds it.</item>
    ///   <item><c>create_account</c> is the approval-gated write the product's own approval tests
    ///     already use — it is declared <c>RequiresApproval = true</c> and household-member does not
    ///     hold it.</item>
    ///   <item><c>household-member</c> is the narrow role: it may chat and read, but holds neither
    ///     <c>tools.finance.create_account</c> nor <c>chat.approvals.manage</c> (Program.cs says so
    ///     in as many words — "a member can be gated but may not clear another member's gate").</item>
    ///   <item>Transactions and budgets are the tenant-scoped reads a second household must see
    ///     nothing on, on top of the module's own data tabs, which the kit probes automatically.</item>
    /// </list>
    /// </summary>
    public override ProductContract Contract { get; } = new(
        ModuleId: "finance",
        ReadTool: "summarize_spending",
        WriteTool: "create_account",
        NarrowRole: "household-member",
        ReadEndpoints: ["/api/finance/transactions", "/api/finance/budgets"],
        WritePrompt: WriteTurn);

    /// <summary>
    /// The turn the kit's approval invariants (S02–S06) send to park <c>create_account</c>.
    /// <para>
    /// The kit's default — the tool name spelled out — routes correctly but leaves the Mock
    /// provider to synthesise <c>type</c>, and its generic placeholder for a required string is
    /// the literal <c>"example"</c>. <c>AccountTools.CreateAccount</c> refuses that
    /// (<c>'example' is not an account type</c>), so the park succeeds and the RELEASE comes back
    /// 422 — S03 failing on a tool refusal rather than on anything about the approval gate.
    /// </para>
    /// <para>
    /// The Mock's documented escape hatch is quoted spans, which fill a tool's required string
    /// parameters in declaration order: the first becomes <c>name</c>, the second <c>type</c>. So
    /// this turn asks for a real account of a real type, and the invariant gets to be about the
    /// gate. The unquoted words still carry the routing: <c>create</c> + <c>account</c>.
    /// </para>
    /// </summary>
    private const string WriteTurn =
        "Please create account 'Kit Conformance Reserve' 'savings' for me, using a tool.";

    /// <summary>
    /// pgvector on the SAME major the AppHost and the compose file ship — a product should not test
    /// against a Postgres it does not run. The kit defaults to pg16.
    /// </summary>
    protected override string PostgresImage => "pgvector/pgvector:pg17";
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<IntegrationFixture>;
