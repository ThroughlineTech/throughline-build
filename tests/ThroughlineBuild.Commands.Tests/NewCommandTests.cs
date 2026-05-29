using System.Text;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

public class NewCommandTests
{
    private static TicketCommandContext MakeCtx(
        Dictionary<string, string>? args = null) =>
        new TicketCommandContext("", args ?? new Dictionary<string, string>());

    [Fact]
    public async Task SuccessOutput_matches_spec_format()
    {
        var tempBodyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempBodyPath, @"# Test Ticket

Some body content.

## Acceptance

- [ ] Item 1

## Out of scope

- Interactive mode
");

            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var options = new BuildOptions(
                SessionId: "test-session",
                WorkerName: "",
                WorkerTimeout: TimeSpan.Zero,
                DebugCaptureDirectory: null);
            var phase = new NewPhase(ticketing, events, options);
            var cmd = new NewCommand(phase, "https://plane.example.com", "workspace", null);

            var ctx = MakeCtx(new Dictionary<string, string> { ["body_path"] = tempBodyPath });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Message);
            Assert.Contains("Created TLB-42 (uuid:", result.Message);
            Assert.Contains("https://plane.example.com/?next_path=/workspace/browse/TLB-42", result.Message);
            Assert.Contains("Warning:", result.Message);
        }
        finally
        {
            if (File.Exists(tempBodyPath))
                File.Delete(tempBodyPath);
        }
    }

    [Fact]
    public async Task TitleOverride_applied()
    {
        var tempBodyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempBodyPath, @"# Original Title

Body content.

## Acceptance

- [ ] Item

## Out of scope

- None
");

            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var options = new BuildOptions(
                SessionId: "test-session",
                WorkerName: "",
                WorkerTimeout: TimeSpan.Zero,
                DebugCaptureDirectory: null);
            var phase = new NewPhase(ticketing, events, options);
            var cmd = new NewCommand(phase, "", "", null);

            var ctx = MakeCtx(new Dictionary<string, string>
            {
                ["body_path"] = tempBodyPath,
                ["title"] = "Override Title"
            });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Message);
            // Verify the override was used (ticket will have the override title)
            var createCall = ticketing.CreateCalls[0];
            Assert.Equal("Override Title", createCall.title);
        }
        finally
        {
            if (File.Exists(tempBodyPath))
                File.Delete(tempBodyPath);
        }
    }

    [Fact]
    public async Task TypeOverride_applied()
    {
        var tempBodyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempBodyPath, @"# Test

Body.

## Acceptance

- [ ] Item

## Out of scope

- None
");

            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var options = new BuildOptions(
                SessionId: "test-session",
                WorkerName: "",
                WorkerTimeout: TimeSpan.Zero,
                DebugCaptureDirectory: null);
            var phase = new NewPhase(ticketing, events, options);
            var cmd = new NewCommand(phase, "", "", null);

            var ctx = MakeCtx(new Dictionary<string, string>
            {
                ["body_path"] = tempBodyPath,
                ["type"] = "bug"
            });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            var createCall = ticketing.CreateCalls[0];
            Assert.Equal("bug", createCall.type);
        }
        finally
        {
            if (File.Exists(tempBodyPath))
                File.Delete(tempBodyPath);
        }
    }

    [Fact]
    public async Task LabelFlags_accumulate()
    {
        var tempBodyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempBodyPath, @"# Test

Body.

## Acceptance

- [ ] Item

## Out of scope

- None
");

            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var options = new BuildOptions(
                SessionId: "test-session",
                WorkerName: "",
                WorkerTimeout: TimeSpan.Zero,
                DebugCaptureDirectory: null);
            var phase = new NewPhase(ticketing, events, options);
            var cmd = new NewCommand(phase, "", "", null);

            var ctx = MakeCtx(new Dictionary<string, string>
            {
                ["body_path"] = tempBodyPath,
                ["labels"] = "label1\tlabel2\tlabel3"
            });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            var createCall = ticketing.CreateCalls[0];
            Assert.NotNull(createCall.labels);
            Assert.Equal(3, createCall.labels!.Count);
            Assert.Contains("label1", createCall.labels);
            Assert.Contains("label2", createCall.labels);
            Assert.Contains("label3", createCall.labels);
        }
        finally
        {
            if (File.Exists(tempBodyPath))
                File.Delete(tempBodyPath);
        }
    }

    [Fact]
    public async Task PrintTemplate_emits_template_to_stdout_and_makes_no_Plane_calls()
    {
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var options = new BuildOptions(
            SessionId: "test-session",
            WorkerName: "",
            WorkerTimeout: TimeSpan.Zero,
            DebugCaptureDirectory: null);
        var phase = new NewPhase(ticketing, events, options);
        var cmd = new NewCommand(phase, "", "", null);

        var ctx = MakeCtx(new Dictionary<string, string> { ["print_template"] = "true" });

        // Capture Console.Out to verify template content.
        var originalOut = Console.Out;
        var captured = new System.IO.StringWriter();
        Console.SetOut(captured);
        CommandResult result;
        try
        {
            result = await cmd.ExecuteAsync(ctx, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = captured.ToString();
        Assert.True(result.Success);
        // Template must contain a top-level heading (NewPhase title marker).
        Assert.Contains("# ", output);
        // Must contain Acceptance criteria section (NewPhase validator looks for this).
        Assert.Contains("Acceptance", output);
        // Must contain Out of scope section.
        Assert.Contains("Out of scope", output);
        // No Plane calls must have been made.
        Assert.Empty(ticketing.CreateCalls);
    }

    [Fact]
    public async Task MissingBodyPath_fails()
    {
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var options = new BuildOptions(
            SessionId: "test-session",
            WorkerName: "",
            WorkerTimeout: TimeSpan.Zero,
            DebugCaptureDirectory: null);
        var phase = new NewPhase(ticketing, events, options);
        var cmd = new NewCommand(phase, "", "", null);

        var ctx = MakeCtx(new Dictionary<string, string>());
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("body_path", result.Message);
    }

    [Fact]
    public async Task MissingBodyFile_fails_with_validation_error()
    {
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var options = new BuildOptions(
            SessionId: "test-session",
            WorkerName: "",
            WorkerTimeout: TimeSpan.Zero,
            DebugCaptureDirectory: null);
        var phase = new NewPhase(ticketing, events, options);
        var cmd = new NewCommand(phase, "", "", null);

        var ctx = MakeCtx(new Dictionary<string, string>
        {
            ["body_path"] = "/nonexistent/path/to/body.md"
        });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("validation error", result.Message);
    }

    [Fact]
    public async Task ValidationWarnings_rendered_with_Warning_prefix()
    {
        var tempBodyPath = Path.GetTempFileName();
        try
        {
            // Body without Acceptance section to trigger warnings.
            await File.WriteAllTextAsync(tempBodyPath, @"# Test

Short body.");

            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var options = new BuildOptions(
                SessionId: "test-session",
                WorkerName: "",
                WorkerTimeout: TimeSpan.Zero,
                DebugCaptureDirectory: null);
            var phase = new NewPhase(ticketing, events, options);
            var cmd = new NewCommand(phase, "", "", null);

            var ctx = MakeCtx(new Dictionary<string, string> { ["body_path"] = tempBodyPath });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Message);
            Assert.Contains("Warning:", result.Message);
        }
        finally
        {
            if (File.Exists(tempBodyPath))
                File.Delete(tempBodyPath);
        }
    }

    [Fact]
    public async Task EmptyPlaneUrl_omits_url_line()
    {
        var tempBodyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempBodyPath, @"# Test

Body.

## Acceptance

- [ ] Item

## Out of scope

- None
");

            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var options = new BuildOptions(
                SessionId: "test-session",
                WorkerName: "",
                WorkerTimeout: TimeSpan.Zero,
                DebugCaptureDirectory: null);
            var phase = new NewPhase(ticketing, events, options);
            var cmd = new NewCommand(phase, "", "", null);

            var ctx = MakeCtx(new Dictionary<string, string> { ["body_path"] = tempBodyPath });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Message);
            Assert.DoesNotContain("https://", result.Message);
            Assert.DoesNotContain("http://", result.Message);
        }
        finally
        {
            if (File.Exists(tempBodyPath))
                File.Delete(tempBodyPath);
        }
    }

    [Fact]
    public async Task DebugCaptureDir_creates_artifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-debug-" + Guid.NewGuid());
        var tempBodyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempBodyPath, @"# Test

Body.

## Acceptance

- [ ] Item

## Out of scope

- None
");

            var ticketing = new FakeTicketing();
            var events = new FakeEventSink();
            var options = new BuildOptions(
                SessionId: "test-session",
                WorkerName: "",
                WorkerTimeout: TimeSpan.Zero,
                DebugCaptureDirectory: null);
            var phase = new NewPhase(ticketing, events, options);
            var cmd = new NewCommand(phase, "", "", tempDir);

            var ctx = MakeCtx(new Dictionary<string, string> { ["body_path"] = tempBodyPath });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(Directory.Exists(tempDir));
            Assert.True(File.Exists(Path.Combine(tempDir, "validation.txt")));
            Assert.True(File.Exists(Path.Combine(tempDir, "body.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "api-response.json")));
        }
        finally
        {
            if (File.Exists(tempBodyPath))
                File.Delete(tempBodyPath);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------- Fakes ----------

    private sealed class FakeTicketing : ITicketing
    {
        public List<(string title, string? type, string descriptionHtml, IReadOnlyList<string>? labels)> CreateCalls { get; } = new();

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
            CancellationToken ct)
        {
            CreateCalls.Add((title, type, descriptionHtml, initialLabelNames));
            return Task.FromResult(new NewTicketResult("TLB-42", "12345678-1234-1234-1234-123456789012", DateTime.UtcNow));
        }

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
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
