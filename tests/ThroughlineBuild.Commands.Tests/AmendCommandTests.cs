using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

public class AmendCommandTests
{
    private static Ticket MakeTicket(
        TicketState state = TicketState.Ready,
        IReadOnlyList<string>? labels = null) => new Ticket(
        Id: "TLB-1",
        Uuid: "test-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>desc</p>",
        Relations: Array.Empty<Relation>(),
        Labels: labels ?? Array.Empty<string>(),
        ParentId: null);

    private static TicketCommandContext MakeCtx(
        string ticketId = "TLB-1",
        Dictionary<string, string>? args = null) =>
        new TicketCommandContext(ticketId, args ?? new Dictionary<string, string>());

    [Fact]
    public async Task SizeOnly_existing_non_size_labels_preserved()
    {
        var ticket = MakeTicket(labels: new[] { "plan-ticket", "risk:low", "size:s" });
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["size"] = "M" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.ApplyLabels);
        var applied = ticketing.ApplyLabels[0].labels;
        Assert.Contains("plan-ticket", applied);
        Assert.Contains("risk:low", applied);
        Assert.Contains("size:m", applied);
        Assert.DoesNotContain("size:s", applied);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Single(events.Events);
        Assert.Equal(EventKind.TicketWrite, events.Events[0].Kind);
    }

    [Fact]
    public async Task NoteOnly_appends_Context_Note_with_today_date_and_verbatim_note()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var ctx = MakeCtx(args: new Dictionary<string, string> { ["note"] = "rationale text" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.AppendDescriptions);
        var html = ticketing.AppendDescriptions[0].html;
        Assert.Contains(today, html);
        Assert.Contains("rationale text", html);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Single(events.Events);
        Assert.Equal(EventKind.TicketWrite, events.Events[0].Kind);
    }

    [Fact]
    public async Task SizeAndNote_size_first_then_note()
    {
        var ticket = MakeTicket(labels: new[] { "plan-ticket" });
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string>
        {
            ["size"] = "L",
            ["note"] = "some rationale"
        });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.ApplyLabels);
        Assert.Single(ticketing.AppendDescriptions);

        // Verify order: size (ApplyLabels) was called before note (AppendDescriptions).
        // Both calls are tracked with a sequence counter in FakeTicketing.
        Assert.True(ticketing.ApplyLabels[0].sequence < ticketing.AppendDescriptions[0].sequence,
            "ApplyLabels must be called before AppendDescriptions");
        Assert.Equal(2, events.Events.Count);
        Assert.All(events.Events, e => Assert.Equal(EventKind.TicketWrite, e.Kind));
    }

    [Fact]
    public async Task Terminal_rejected_no_writes()
    {
        var ticket = MakeTicket(state: TicketState.Done);
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["size"] = "M" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("Cancelled or Done", result.Message);
        Assert.Contains("reopen first", result.Message);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Terminal_Cancelled_rejected_no_writes()
    {
        var ticket = MakeTicket(state: TicketState.Cancelled);
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["size"] = "M" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("Cancelled or Done", result.Message);
        Assert.Contains("reopen first", result.Message);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task MissingFlags_rejected()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string>());
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("at least one", result.Message);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task InvalidSize_rejected()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["size"] = "XL" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task ScalarMetadata_updates_title_normalized_priority_and_type()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);
        var ctx = MakeCtx(args: new Dictionary<string, string>
        {
            ["title"] = "A sharper title",
            ["priority"] = "HIGH",
            ["type"] = "Bug"
        });

        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(("TLB-1", "A sharper title"), Assert.Single(ticketing.TitleUpdates));
        Assert.Equal(("TLB-1", "high"), Assert.Single(ticketing.PriorityUpdates));
        Assert.Equal(("TLB-1", "Bug"), Assert.Single(ticketing.TypeUpdates));
        Assert.Equal(3, events.Events.Count);
    }

    [Theory]
    [InlineData("urgent", "urgent")]
    [InlineData("High", "high")]
    [InlineData("MEDIUM", "medium")]
    [InlineData("low", "low")]
    [InlineData("None", "none")]
    public async Task Priority_accepts_case_insensitive_supported_values(string supplied, string expected)
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var cmd = new AmendCommand(ticketing, new FakeEventSink());

        var result = await cmd.ExecuteAsync(
            MakeCtx(args: new Dictionary<string, string> { ["priority"] = supplied }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(expected, Assert.Single(ticketing.PriorityUpdates).priority);
    }

    [Fact]
    public async Task InvalidPriority_rejected_before_ticket_read_or_write()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var cmd = new AmendCommand(ticketing, new FakeEventSink());

        var result = await cmd.ExecuteAsync(
            MakeCtx(args: new Dictionary<string, string> { ["priority"] = "critical" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("urgent, high, medium, low, or none", result.Message);
        Assert.Equal(0, ticketing.GetCalls);
        Assert.Empty(ticketing.PriorityUpdates);
    }

    [Fact]
    public async Task UnknownType_preflight_prevents_earlier_scalar_writes()
    {
        var ticketing = new FakeTicketing(MakeTicket()) { UnknownType = "Missing" };
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var result = await cmd.ExecuteAsync(
            MakeCtx(args: new Dictionary<string, string>
            {
                ["title"] = "Must not be written",
                ["priority"] = "high",
                ["type"] = "Missing"
            }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Issue type 'Missing' not found", result.Message);
        Assert.Empty(ticketing.TitleUpdates);
        Assert.Empty(ticketing.PriorityUpdates);
        Assert.Empty(ticketing.TypeUpdates);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task UnknownAddedLabel_preflight_prevents_earlier_scalar_writes()
    {
        var ticketing = new FakeTicketing(MakeTicket(labels: ["keep"]));
        ticketing.UnknownLabels.Add("missing");
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);
        var ctx = new TicketCommandContext(
            "TLB-1",
            new Dictionary<string, string> { ["priority"] = "urgent" },
            new Dictionary<string, IReadOnlyList<string>> { ["label-add"] = ["missing"] });

        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Label 'missing' not found", result.Message);
        Assert.Empty(ticketing.PriorityUpdates);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Empty(events.Events);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("type")]
    [InlineData("parent")]
    public async Task RequiredMetadataValues_reject_whitespace_before_ticket_read(string option)
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var cmd = new AmendCommand(ticketing, new FakeEventSink());

        var result = await cmd.ExecuteAsync(
            MakeCtx(args: new Dictionary<string, string> { [option] = "   " }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, ticketing.GetCalls);
    }

    [Fact]
    public async Task Parent_rejects_self_parent_before_ticket_read()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var cmd = new AmendCommand(ticketing, new FakeEventSink());

        var result = await cmd.ExecuteAsync(
            MakeCtx(args: new Dictionary<string, string> { ["parent"] = "tlb-1" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("own parent", result.Message);
        Assert.Equal(0, ticketing.GetCalls);
    }

    [Fact]
    public async Task RepeatableLabelEdits_preserve_unrelated_labels_and_do_not_duplicate()
    {
        var ticket = MakeTicket(labels: ["keep", "stale", "already"]);
        var ticketing = new FakeTicketing(ticket);
        var cmd = new AmendCommand(ticketing, new FakeEventSink());
        var ctx = new TicketCommandContext(
            "TLB-1",
            new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["label-add"] = ["new-one", "new-two", "ALREADY"],
                ["label-remove"] = ["stale", "absent"]
            });

        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        var applied = Assert.Single(ticketing.ApplyLabels).labels;
        Assert.Equal(["keep", "already", "new-one", "new-two"], applied);
    }

    [Fact]
    public async Task Parent_resolves_both_ticket_ids_to_uuids()
    {
        var child = MakeTicket();
        var parent = child with { Id = "TLB-9", Uuid = "parent-uuid" };
        var ticketing = new FakeTicketing(child, parent);
        var cmd = new AmendCommand(ticketing, new FakeEventSink());

        var result = await cmd.ExecuteAsync(
            MakeCtx(args: new Dictionary<string, string> { ["parent"] = "TLB-9" }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(("test-uuid-1", "parent-uuid"), Assert.Single(ticketing.ParentUpdates));
        Assert.Equal(["TLB-1", "TLB-9"], ticketing.GetIds);
    }

    [Fact]
    public async Task Parent_rejects_cross_project_alias_before_write()
    {
        var child = MakeTicket();
        var parent = child with { Id = "OTHER-9", Uuid = "parent-uuid" };
        var ticketing = new FakeTicketing(child, parent);
        var cmd = new AmendCommand(ticketing, new FakeEventSink());

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => cmd.ExecuteAsync(
            MakeCtx(args: new Dictionary<string, string> { ["parent"] = "OTHER-9" }),
            CancellationToken.None));

        Assert.Contains("outside configured project", error.Message);
        Assert.Empty(ticketing.ParentUpdates);
        Assert.Equal(["TLB-1", "OTHER-9"], ticketing.GetIds);
    }

    [Fact]
    public async Task DescriptionOnly_calls_UpdateDescriptionAsync()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(tempFile, "<p>new description</p>");
            var ctx = MakeCtx(args: new Dictionary<string, string> { ["description"] = tempFile });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(ticketing.UpdateDescriptions);
            Assert.Equal("<p>new description</p>", ticketing.UpdateDescriptions[0].html);
            Assert.Empty(ticketing.ApplyLabels);
            Assert.Empty(ticketing.AppendDescriptions);
            Assert.Single(events.Events);
            Assert.Equal(EventKind.TicketWrite, events.Events[0].Kind);
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AcOnly_calls_UpdateDescriptionAsync()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(tempFile, "<h3>Acceptance Criteria</h3><ul><li>foo</li></ul>");
            var ctx = MakeCtx(args: new Dictionary<string, string> { ["ac"] = tempFile });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(ticketing.UpdateDescriptions);
            Assert.Equal("<h3>Acceptance Criteria</h3><ul><li>foo</li></ul>", ticketing.UpdateDescriptions[0].html);
            Assert.Empty(ticketing.ApplyLabels);
            Assert.Empty(ticketing.AppendDescriptions);
            Assert.Single(events.Events);
            Assert.Equal(EventKind.TicketWrite, events.Events[0].Kind);
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DescriptionAndSize_size_then_description()
    {
        var ticket = MakeTicket(labels: new[] { "plan-ticket" });
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(tempFile, "<p>updated desc</p>");
            var ctx = MakeCtx(args: new Dictionary<string, string>
            {
                ["size"] = "M",
                ["description"] = tempFile
            });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(ticketing.ApplyLabels);
            Assert.Single(ticketing.UpdateDescriptions);
            Assert.Empty(ticketing.AppendDescriptions);

            // Verify order: size (ApplyLabels) was called before description (UpdateDescriptions).
            Assert.True(ticketing.ApplyLabels[0].sequence < ticketing.UpdateDescriptions[0].sequence,
                "ApplyLabels must be called before UpdateDescriptions");
            Assert.Equal(2, events.Events.Count);
            Assert.All(events.Events, e => Assert.Equal(EventKind.TicketWrite, e.Kind));
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task MissingDescriptionFile_produces_error()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string>
        {
            ["title"] = "Must not be written",
            ["description"] = "/nonexistent/path/file.txt"
        });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("failed to read description file", result.Message);
        Assert.Empty(ticketing.TitleUpdates);
        Assert.Equal(0, ticketing.GetCalls);
        Assert.Empty(ticketing.UpdateDescriptions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task MissingAcFile_produces_error()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string>
        {
            ["title"] = "Must not be written",
            ["ac"] = "/nonexistent/path/ac.txt"
        });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("failed to read ac file", result.Message);
        Assert.Empty(ticketing.TitleUpdates);
        Assert.Equal(0, ticketing.GetCalls);
        Assert.Empty(ticketing.UpdateDescriptions);
        Assert.Empty(events.Events);
    }

    // ---------- Fakes ----------

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Dictionary<string, Ticket> _tickets;
        private int _seq;

        public List<(string id, IReadOnlyList<string> labels, int sequence)> ApplyLabels { get; } = new();
        public List<(string id, string html, int sequence)> AppendDescriptions { get; } = new();
        public List<(string id, string html, int sequence)> UpdateDescriptions { get; } = new();
        public List<(string id, string title)> TitleUpdates { get; } = new();
        public List<(string id, string priority)> PriorityUpdates { get; } = new();
        public List<(string id, string type)> TypeUpdates { get; } = new();
        public List<(string childUuid, string parentUuid)> ParentUpdates { get; } = new();
        public List<string> GetIds { get; } = new();
        public int GetCalls => GetIds.Count;
        public HashSet<string> UnknownLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? UnknownType { get; init; }

        public FakeTicketing(params Ticket[] tickets) =>
            _tickets = tickets.ToDictionary(ticket => ticket.Id, StringComparer.OrdinalIgnoreCase);

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct)
        {
            GetIds.Add(id);
            return Task.FromResult(_tickets[id]);
        }

        public Task<Ticket> GetRelationTicketAsync(string id, CancellationToken ct)
        {
            if (id.StartsWith("OTHER-", StringComparison.OrdinalIgnoreCase))
            {
                GetIds.Add(id);
                return Task.FromException<Ticket>(
                    new KeyNotFoundException($"Ticket '{id}' is outside configured project 'TLB'"));
            }
            return GetAsync(id, ct);
        }

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(ids.Select(id => _tickets[id]).ToList());

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            Task.CompletedTask;

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
        {
            AppendDescriptions.Add((id, html, ++_seq));
            return Task.CompletedTask;
        }

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            Task.FromResult("comment-1");

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct)
        {
            ApplyLabels.Add((id, labels.ToList(), ++_seq));
            return Task.CompletedTask;
        }

        public Task ValidateLabelsAsync(IEnumerable<string> labels, CancellationToken ct)
        {
            var missing = labels.FirstOrDefault(UnknownLabels.Contains);
            return missing is null
                ? Task.CompletedTask
                : Task.FromException(new ArgumentException($"Label '{missing}' not found in Plane project"));
        }

        public Task UpdateTitleAsync(string id, string title, CancellationToken ct)
        {
            TitleUpdates.Add((id, title));
            return Task.CompletedTask;
        }

        public Task UpdatePriorityAsync(string id, string priority, CancellationToken ct)
        {
            PriorityUpdates.Add((id, priority));
            return Task.CompletedTask;
        }

        public Task UpdateTypeAsync(string id, string type, CancellationToken ct)
        {
            TypeUpdates.Add((id, type));
            return Task.CompletedTask;
        }

        public Task ValidateTypeAsync(string type, CancellationToken ct) =>
            string.Equals(type, UnknownType, StringComparison.OrdinalIgnoreCase)
                ? Task.FromException(new ArgumentException($"Issue type '{type}' not found in Plane project"))
                : Task.CompletedTask;

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<TicketComment>)Array.Empty<TicketComment>());

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct)
        {
            ParentUpdates.Add((childUuid, parentUuid));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct)
        {
            UpdateDescriptions.Add((id, html, ++_seq));
            return Task.CompletedTask;
        }

        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
                string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
                Task.FromResult(new CreateChildTicketsResult(
                    children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                    Array.Empty<string>()));
    }

    private sealed class FakeEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();

        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
