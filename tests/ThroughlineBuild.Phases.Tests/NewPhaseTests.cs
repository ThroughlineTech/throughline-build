using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class NewPhaseTests
{
    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-1",
        WorkerName: "test-worker",
        WorkerTimeout: TimeSpan.FromMinutes(5));

    [Fact]
    public async Task RunAsync_ValidBodyWithTitle_CreatesTicketAndReturnsResult()
    {
        var bodyContent = @"# Create User API

**Type:** feature

## Description
This feature adds a REST endpoint for user creation.

## Acceptance
- [ ] POST /api/users creates a user
- [ ] Validates required fields
- [ ] Returns 201 on success

## Out of Scope
- User authentication
- Email verification
";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var result = await phase.RunAsync(
                new NewPhaseOptions(tempFile),
                CancellationToken.None);

            Assert.Equal("TLB-42", result.Id);
            Assert.Equal("uuid-123", result.Uuid);
            Assert.Empty(result.ValidationWarnings);

            // Verify CreateTicketAsync was called with correct parameters.
            Assert.Single(ticketing.CreatedTickets);
            var created = ticketing.CreatedTickets[0];
            Assert.Equal("Create User API", created.title);
            Assert.Null(created.type);
            Assert.Equal(bodyContent, created.descriptionHtml);
            Assert.Null(created.initialLabelNames);

            // Verify events were emitted.
            var workerSpawns = events.Events.Where(e => e.Kind == EventKind.WorkerSpawn).ToList();
            var ticketWrites = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
            Assert.Single(workerSpawns);
            Assert.Single(ticketWrites);

            var spawn = workerSpawns[0];
            Assert.Equal("deterministic", spawn.Data["worker"]);
            Assert.Equal("creator", spawn.Data["role"]);

            var write = ticketWrites[0];
            Assert.Equal("create_ticket", write.Data["action"]);
            Assert.Equal("TLB-42", write.Data["id"]);
            Assert.Equal("uuid-123", write.Data["uuid"]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_BodyWithAtxHeadingTitle_ParsesTitle()
    {
        var bodyContent = @"# Task Title

Content here.

## Acceptance
- Test 1
";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var result = await phase.RunAsync(
                new NewPhaseOptions(tempFile),
                CancellationToken.None);

            Assert.Equal("TLB-42", result.Id);
            Assert.Equal("Task Title", ticketing.CreatedTickets[0].title);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_BodyWithAsteriskTitleField_ParsesTitle()
    {
        var bodyContent = @"**Title:** My Feature Request

Some description.

## Acceptance
- Requirement 1
";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var result = await phase.RunAsync(
                new NewPhaseOptions(tempFile),
                CancellationToken.None);

            Assert.Equal("My Feature Request", ticketing.CreatedTickets[0].title);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_MissingTitle_ThrowsValidationException()
    {
        var bodyContent = "Just some content with no title header.";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var ex = await Assert.ThrowsAsync<NewPhaseValidationException>(
                () => phase.RunAsync(new NewPhaseOptions(tempFile), CancellationToken.None));

            Assert.Contains("No title found", string.Join("; ", ex.Errors));
            Assert.Empty(ticketing.CreatedTickets);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_EmptyBody_ThrowsValidationException()
    {
        var bodyContent = "";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var ex = await Assert.ThrowsAsync<NewPhaseValidationException>(
                () => phase.RunAsync(new NewPhaseOptions(tempFile), CancellationToken.None));

            Assert.Contains("empty", string.Join("; ", ex.Errors).ToLowerInvariant());
            Assert.Empty(ticketing.CreatedTickets);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_MissingBodyFile_ThrowsValidationException()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}.md");

        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new NewPhase(ticketing, events, MakeOptions());

        var ex = await Assert.ThrowsAsync<NewPhaseValidationException>(
            () => phase.RunAsync(new NewPhaseOptions(nonExistentPath), CancellationToken.None));

        Assert.Contains("not found", string.Join("; ", ex.Errors).ToLowerInvariant());
        Assert.Empty(ticketing.CreatedTickets);
    }

    [Fact]
    public async Task RunAsync_BodyWithoutAcceptance_ProducesWarningButCreates()
    {
        var bodyContent = @"# Feature Request

This is a feature request without acceptance criteria.

## Out of Scope
- Nothing out of scope.
";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var result = await phase.RunAsync(
                new NewPhaseOptions(tempFile),
                CancellationToken.None);

            Assert.Equal("TLB-42", result.Id);
            Assert.Single(ticketing.CreatedTickets);
            Assert.Contains("Acceptance", result.ValidationWarnings.FirstOrDefault() ?? "");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_ShortBody_ProducesWarningButCreates()
    {
        var bodyContent = @"# Short

Body under 200 chars.

## Acceptance
- Done
";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var result = await phase.RunAsync(
                new NewPhaseOptions(tempFile),
                CancellationToken.None);

            Assert.Equal("TLB-42", result.Id);
            Assert.Single(ticketing.CreatedTickets);
            Assert.Contains("shorter than 200 characters", result.ValidationWarnings.FirstOrDefault() ?? "");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_OverrideTitleWins()
    {
        var bodyContent = @"# Original Title

Some content.

## Acceptance
- Requirement
";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var result = await phase.RunAsync(
                new NewPhaseOptions(tempFile, OverrideTitle: "Overridden Title"),
                CancellationToken.None);

            Assert.Equal("Overridden Title", ticketing.CreatedTickets[0].title);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_OverrideTypeWins()
    {
        var bodyContent = @"# Task

Content.

## Acceptance
- Requirement
";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var result = await phase.RunAsync(
                new NewPhaseOptions(tempFile, OverrideType: "bug"),
                CancellationToken.None);

            Assert.Equal("bug", ticketing.CreatedTickets[0].type);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_InitialLabelsPassedThrough()
    {
        var bodyContent = @"# Feature

Content with acceptance.

## Acceptance
- Done

## Out of Scope
- Nothing
";
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.md");
        await File.WriteAllTextAsync(tempFile, bodyContent);

        try
        {
            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var phase = new NewPhase(ticketing, events, MakeOptions());

            var labels = new[] { "team:core", "priority:high" }.AsReadOnly();
            var result = await phase.RunAsync(
                new NewPhaseOptions(tempFile, InitialLabels: labels),
                CancellationToken.None);

            var created = ticketing.CreatedTickets[0];
            Assert.NotNull(created.initialLabelNames);
            Assert.Equal(2, created.initialLabelNames.Count);
            Assert.Contains("team:core", created.initialLabelNames);
            Assert.Contains("priority:high", created.initialLabelNames);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_ExplicitInterfaceMethod_ThrowsNotSupported()
    {
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new NewPhase(ticketing, events, MakeOptions());

        var iface = (IWorkflowPhase)phase;
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => iface.RunAsync("TLB-1", "/tmp", CancellationToken.None));

        Assert.Contains("NewPhase creates new tickets", ex.Message);
    }

    private sealed class FakeTicketing : ITicketing
    {
        public List<(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames)>
            CreatedTickets { get; } = new();

        public BackendCapabilities Capabilities =>
            new BackendCapabilities(true, true, true, false);

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
            CancellationToken ct)
        {
            CreatedTickets.Add((title, type, descriptionHtml, initialLabelNames));
            return Task.FromResult(new NewTicketResult("TLB-42", "uuid-123", DateTime.UtcNow));
        }

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;
    
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
