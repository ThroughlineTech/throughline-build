using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public sealed class ChainDryRunPlannerTests
{
    [Fact]
    public async Task MultiLevelTree_PreservesPostOrderAndExactOutput()
    {
        var root = MakeTicket("TLB-1", "root");
        var child = MakeTicket("TLB-2", "child");
        var grandchild = MakeTicket("TLB-3", "grandchild");
        var rootLeaf = MakeTicket("TLB-4", "root-leaf");
        var ticketing = new DryRunTicketing(root);
        ticketing.SeedChildren(root.Uuid, child, rootLeaf);
        ticketing.SeedChildren(child.Uuid, grandchild);

        var (plan, output) = await RunPlannerAsync(ticketing, root, maxDepth: 16);

        Assert.Equal(new[] { "TLB-3", "TLB-2", "TLB-4", "TLB-1" },
            plan.PostOrder.Select(item => item.Ticket.Id));
        Assert.Equal(new[] { false, true, false, true },
            plan.PostOrder.Select(item => item.HasLiveChildren));
        Assert.Equal(Lines(
            "[TLB-1] dry-run chain plan (max depth 16):",
            "post-order schedule:",
            "  1.     TLB-3 - run plan/implement/review/ship",
            "  2.   TLB-2 - roll up internal node on chain/tlb-2",
            "  3.   TLB-4 - run plan/implement/review/ship",
            "  4. TLB-1 - roll up internal node on chain/tlb-1",
            "branch topology:",
            "  chain/tlb-1 from main integrates subtree for TLB-1",
            "  chain/tlb-2 from chain/tlb-1 integrates subtree for TLB-2",
            "  ticket/tlb-3 from chain/tlb-2 before TLB-3",
            "  ticket/tlb-4 from chain/tlb-1 before TLB-4"), output);
    }

    [Fact]
    public async Task DepthCappedTree_PreservesWarningAndExactOutput()
    {
        var root = MakeTicket("TLB-1", "root");
        var child = MakeTicket("TLB-2", "child");
        var grandchild = MakeTicket("TLB-3", "grandchild");
        var ticketing = new DryRunTicketing(root);
        ticketing.SeedChildren(root.Uuid, child);
        ticketing.SeedChildren(child.Uuid, grandchild);

        var (plan, output) = await RunPlannerAsync(ticketing, root, maxDepth: 1);

        Assert.Equal(new[] { "TLB-2", "TLB-1" },
            plan.PostOrder.Select(item => item.Ticket.Id));
        Assert.Equal(new[] { true, true },
            plan.PostOrder.Select(item => item.HasLiveChildren));
        Assert.Equal(new[] { "depth cap 1 reached at TLB-2; subtree omitted" }, plan.Warnings);
        Assert.Equal(Lines(
            "[TLB-1] dry-run chain plan (max depth 1):",
            "post-order schedule:",
            "  1.   TLB-2 - roll up internal node on chain/tlb-2",
            "  2. TLB-1 - roll up internal node on chain/tlb-1",
            "branch topology:",
            "  chain/tlb-1 from main integrates subtree for TLB-1",
            "  chain/tlb-2 from chain/tlb-1 integrates subtree for TLB-2",
            "warnings:",
            "  depth cap 1 reached at TLB-2; subtree omitted"), output);
    }

    [Fact]
    public async Task SingleLeaf_PreservesLeafDistinctionAndExactOutput()
    {
        var root = MakeTicket("TLB-1", "root");
        var ticketing = new DryRunTicketing(root);

        var (plan, output) = await RunPlannerAsync(ticketing, root, maxDepth: 16);

        var item = Assert.Single(plan.PostOrder);
        Assert.False(item.HasLiveChildren);
        Assert.Equal(Lines(
            "[TLB-1] dry-run chain plan (max depth 16):",
            "post-order schedule:",
            "  1. TLB-1 - run plan/implement/review/ship",
            "branch topology:",
            "  ticket/tlb-1 from main before TLB-1"), output);
    }

    private static async Task<(DryRunPlan Plan, string Output)> RunPlannerAsync(
        DryRunTicketing ticketing,
        Ticket root,
        int maxDepth)
    {
        var output = new StringWriter();
        var planner = new ChainDryRunPlanner(ticketing, output);
        var plan = await planner.BuildAsync(
            root,
            targetBranch: "main",
            maxDepth,
            CancellationToken.None);
        planner.Print(plan, maxDepth);
        return (plan, output.ToString());
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static Ticket MakeTicket(string id, string uuid) =>
        new(
            id,
            uuid,
            id,
            "feature",
            TicketState.Backlog,
            Size.S,
            Risk.Low,
            "<p>description</p>",
            Array.Empty<Relation>(),
            Array.Empty<string>(),
            null);

    private sealed class DryRunTicketing : ITicketing
    {
        private readonly Ticket _root;
        private readonly Dictionary<string, IReadOnlyList<Ticket>> _children = new(StringComparer.Ordinal);

        public DryRunTicketing(Ticket root) => _root = root;

        public void SeedChildren(string parentUuid, params Ticket[] children) =>
            _children[parentUuid] = children;

        public BackendCapabilities Capabilities => new(false, false, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult(_root);

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult(
                query.ParentId is not null && _children.TryGetValue(query.ParentId, out var children)
                    ? children
                    : (IReadOnlyList<Ticket>)Array.Empty<Ticket>());

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(
            IEnumerable<string> ids,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ApplyLabelsAsync(
            string id,
            IEnumerable<string> labels,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AddRelationAsync(
            string blockedId,
            string blockerId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(
            string id,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetParentAsync(
            string childUuid,
            string parentUuid,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TransitionLifecycleAsync(
            string id,
            LifecycleTransition transition,
            string? reason,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateDescriptionAsync(
            string id,
            string html,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid,
            IReadOnlyList<ChildTicketSpec> children,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
