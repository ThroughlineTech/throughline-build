using System.Text;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

public class ListCommandTests
{
    private static Ticket MakeTicket(
        string id = "TLB-1",
        string title = "Test ticket",
        string type = "feature",
        TicketState state = TicketState.Ready,
        string? parentId = null) => new Ticket(
        Id: id,
        Title: title,
        Type: type,
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>desc</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: parentId);

    private static TicketCommandContext MakeCtx(
        Dictionary<string, string>? args = null) =>
        new TicketCommandContext("", args ?? new Dictionary<string, string>());

    [Fact]
    public async Task NoFilters_returns_all_tickets()
    {
        var tickets = new[]
        {
            MakeTicket("TLB-1", "First ticket"),
            MakeTicket("TLB-2", "Second ticket"),
            MakeTicket("TLB-3", "Third ticket")
        };
        var ticketing = new FakeTicketing(tickets);
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        var outputText = output.ToString();
        Assert.Contains("ID", outputText);
        Assert.Contains("Title", outputText);
        Assert.Contains("State", outputText);
        Assert.Contains("Type", outputText);
        Assert.Contains("Parent", outputText);
        Assert.Contains("TLB-1", outputText);
        Assert.Contains("TLB-2", outputText);
        Assert.Contains("TLB-3", outputText);
    }

    [Fact]
    public async Task StateFilter_returns_only_matching_tickets()
    {
        var tickets = new[]
        {
            MakeTicket("TLB-1", "Ready ticket", state: TicketState.Ready),
            MakeTicket("TLB-2", "InProgress ticket", state: TicketState.InProgress),
            MakeTicket("TLB-3", "Another Ready", state: TicketState.Ready)
        };
        var ticketing = new FakeTicketing(tickets);
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["state"] = "Ready" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        var outputText = output.ToString();
        Assert.Contains("TLB-1", outputText);
        Assert.Contains("TLB-3", outputText);
        Assert.DoesNotContain("TLB-2", outputText);
        // Verify the query was made with correct filter
        Assert.NotNull(ticketing.LastQuery);
        Assert.Equal(TicketState.Ready, ticketing.LastQuery.State);
    }

    [Fact]
    public async Task ParentFilter_returns_only_child_tickets()
    {
        var tickets = new[]
        {
            MakeTicket("TLB-1", "Child of TLB-10", parentId: "TLB-10"),
            MakeTicket("TLB-2", "Child of TLB-20", parentId: "TLB-20"),
            MakeTicket("TLB-3", "Child of TLB-10", parentId: "TLB-10")
        };
        var ticketing = new FakeTicketing(tickets);
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["parent"] = "TLB-10" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        var outputText = output.ToString();
        Assert.Contains("TLB-1", outputText);
        Assert.Contains("TLB-3", outputText);
        Assert.NotNull(ticketing.LastQuery);
        Assert.Equal("TLB-10", ticketing.LastQuery.ParentId);
    }

    [Fact]
    public async Task TypeFilter_returns_only_matching_tickets()
    {
        var tickets = new[]
        {
            MakeTicket("TLB-1", "Feature ticket", type: "feature"),
            MakeTicket("TLB-2", "Bug ticket", type: "bug"),
            MakeTicket("TLB-3", "Another feature", type: "feature")
        };
        var ticketing = new FakeTicketing(tickets);
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["type"] = "feature" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        var outputText = output.ToString();
        Assert.Contains("TLB-1", outputText);
        Assert.Contains("TLB-3", outputText);
        Assert.NotNull(ticketing.LastQuery);
        Assert.Equal("feature", ticketing.LastQuery.Type);
    }

    [Fact]
    public async Task EmptyResultSet_prints_no_tickets_found()
    {
        var ticketing = new FakeTicketing(Array.Empty<Ticket>());
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        var outputText = output.ToString();
        Assert.Contains("no tickets found", outputText);
    }

    [Fact]
    public async Task TabularOutput_contains_column_headers()
    {
        var tickets = new[] { MakeTicket("TLB-1", "Test") };
        var ticketing = new FakeTicketing(tickets);
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        var outputText = output.ToString();
        // Verify all headers are present
        Assert.Contains("ID", outputText);
        Assert.Contains("Title", outputText);
        Assert.Contains("State", outputText);
        Assert.Contains("Type", outputText);
        Assert.Contains("Parent", outputText);
    }

    [Fact]
    public async Task InvalidState_returns_failure()
    {
        var ticketing = new FakeTicketing(Array.Empty<Ticket>());
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["state"] = "InvalidState" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("invalid", result.Message!.ToLowerInvariant());
    }

    [Fact]
    public async Task ApiException_returns_failure_with_message()
    {
        var ticketing = new FakeTicketingThrows();
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("API error", result.Message!);
    }

    [Fact]
    public async Task TitleTruncation_at_50_chars()
    {
        var longTitle = "This is a very long title that exceeds fifty characters definitely";
        var tickets = new[] { MakeTicket("TLB-1", longTitle) };
        var ticketing = new FakeTicketing(tickets);
        var output = new StringWriter();
        var cmd = new ListCommand(ticketing, output);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        var outputText = output.ToString();
        Assert.Contains("...", outputText);
        // Should not contain the full long title
        Assert.DoesNotContain(longTitle, outputText);
    }

    // ---------- Fakes ----------

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket[] _tickets;

        public TicketQuery? LastQuery { get; private set; }

        public FakeTicketing(Ticket[] tickets)
        {
            _tickets = tickets;
        }

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct)
        {
            LastQuery = query;
            // Filter tickets based on query criteria
            var results = _tickets.AsEnumerable();

            if (query.State.HasValue)
                results = results.Where(t => t.State == query.State.Value);

            if (!string.IsNullOrWhiteSpace(query.ParentId))
                results = results.Where(t => t.ParentId == query.ParentId);

            if (!string.IsNullOrWhiteSpace(query.Type))
                results = results.Where(t => t.Type == query.Type);

            return Task.FromResult<IReadOnlyList<Ticket>>(results.ToList());
        }

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeTicketingThrows : ITicketing
    {
        public BackendCapabilities Capabilities => throw new NotImplementedException();

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            throw new InvalidOperationException("API error");

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
