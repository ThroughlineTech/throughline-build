using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

public record ChildSpec(
    string Title,
    string Description,
    string AcceptanceCriteria,
    string Size,
    string ScopeBoundary);

public record DecomposeResult(
    bool Success,
    string TicketId,
    IReadOnlyList<ChildSpec>? ChildSpecs,
    string? FailureReason,
    IReadOnlyList<string>? CreatedIds = null,
    IReadOnlyList<string>? VerdictFailures = null);

public class DecomposePhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IWorkerAgent _worker;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly IGitClient _git;
    private readonly ProjectContext _project;

    public DecomposePhase(
        ITicketing ticketing,
        IWorkerAgent worker,
        IEventSink events,
        BuildOptions options,
        IGitClient? gitClient = null,
        ProjectContext? project = null)
    {
        _ticketing = ticketing;
        _worker = worker;
        _events = events;
        _options = options;
        _git = gitClient ?? new ProcessGitClient();
        _project = project ?? ProjectContext.Empty;
    }

    public Phase Phase => Phase.Decompose;

    public async Task<DecomposeResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        string mainSha;
        try
        {
            (_, mainSha) = await BaseRefResolver.ResolveAsync(_git, workingDirectory, _options.TargetBranch, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DecomposeResult(false, ticketId, null, $"git rev-parse failed: {ex.Message}");
        }

        var topLevelEntries = Directory.EnumerateFileSystemEntries(workingDirectory)
            .ToList()
            .AsReadOnly();

        var repoState = new RepoState(mainSha, topLevelEntries);
        var brief = DecomposeBriefBuilder.Build(_worker.Name, ticket, repoState, _project);

        await EmitAsync(EventKind.WorkerSpawn, ticketId, new Dictionary<string, object>
        {
            ["worker"] = _options.WorkerName
        }, ct).ConfigureAwait(false);

        var workerOptions = new WorkerOptions(_options.WorkerTimeout, _options.WorkerAllowedTools,
            DebugCaptureDirectory: _options.DebugCaptureDirectory,
            LiveStdoutSink: _options.LiveStdoutSink,
            LiveStderrSink: _options.LiveStderrSink,
            ProgressDigestSink: _options.ProgressDigestSink,
            Size: WorkerSizeMapper.FromTicketSize(ticket.Size));
        var workerResult = await _worker.ExecuteAsync(brief, workingDirectory, workerOptions, ct).ConfigureAwait(false);

        await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
        {
            ["status"] = workerResult.Status.ToString()
        }, ct).ConfigureAwait(false);

        if (workerResult.Status != Status.Ok)
            return new DecomposeResult(false, ticketId, null,
                workerResult.FailureReason ?? workerResult.Status.ToString());

        if (workerResult.Metadata.TryGetValue("llm_usage", out var usageObj))
        {
            var llmData = LlmUsageFlattener.Flatten(usageObj);
            if (llmData is not null)
                await EmitAsync(EventKind.LlmCall, ticketId, llmData, ct).ConfigureAwait(false);
        }

        var childSpecs = TryExtractChildSpecs(workerResult.Metadata);
        if (childSpecs is null)
            return new DecomposeResult(false, ticketId, null,
                "worker metadata missing or malformed child_specs array");
        if (childSpecs.Count < 2)
            return new DecomposeResult(false, ticketId, null,
                $"worker returned {childSpecs.Count} child spec(s); at least 2 are required");

        // Apply verdict checks on child specs
        var verdictFailures = DecomposeVerdict.Check(childSpecs);
        if (verdictFailures.Count > 0)
        {
            await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
            {
                ["status"] = "VerdictFailed",
                ["checks_failed"] = verdictFailures
            }, ct).ConfigureAwait(false);
            return new DecomposeResult(false, ticketId, childSpecs,
                string.Join("; ", verdictFailures), VerdictFailures: verdictFailures);
        }

        await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
        {
            ["status"] = "VerdictPassed"
        }, ct).ConfigureAwait(false);

        // Map ChildSpec -> ChildTicketSpec and create sub-issues in Plane
        var childTicketSpecs = childSpecs.Select(cs =>
        {
            var descHtml = BuildChildDescriptionHtml(cs.Description, cs.AcceptanceCriteria);
            var labelNames = SizeLabelNames(cs.Size);
            return new ChildTicketSpec(cs.Title, descHtml, labelNames);
        }).ToList().AsReadOnly();

        var createResult = await _ticketing.CreateChildTicketsAsync(
            ticket.Uuid, childTicketSpecs, ct).ConfigureAwait(false);

        if (createResult.Created.Count == 0 && createResult.Failures.Count > 0)
            return new DecomposeResult(false, ticketId, childSpecs,
                $"all child ticket creations failed: {string.Join("; ", createResult.Failures)}");

        await _ticketing.CreateCommentAsync(
            ticketId,
            $"<p>[decomposed_at: {mainSha}]</p>",
            ct).ConfigureAwait(false);

        var createdIds = createResult.Created.Select(c => c.Id).ToList().AsReadOnly();
        return new DecomposeResult(true, ticketId, childSpecs, null, createdIds, VerdictFailures: Array.Empty<string>());
    }

    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var result = await RunAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        var outputs = result.Success
            ? new Dictionary<string, string>
            {
                ["child_spec_count"] = result.ChildSpecs!.Count.ToString(),
                ["created_child_count"] = (result.CreatedIds?.Count ?? 0).ToString()
            } as IReadOnlyDictionary<string, string>
            : new Dictionary<string, string>() as IReadOnlyDictionary<string, string>;
        return new PhaseResult(result.Success, result.TicketId, Phase.Decompose, result.FailureReason, outputs);
    }

    private async Task EmitAsync(EventKind kind, string ticketId, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            ticketId,
            Phase.Decompose,
            data), ct).ConfigureAwait(false);
    }

    private static string BuildChildDescriptionHtml(string description, string acceptanceCriteria)
    {
        if (string.IsNullOrEmpty(description) && string.IsNullOrEmpty(acceptanceCriteria))
            return string.Empty;
        if (string.IsNullOrEmpty(acceptanceCriteria))
            return $"<p>{description}</p>";
        if (string.IsNullOrEmpty(description))
            return $"<h3>Acceptance criteria</h3><p>{acceptanceCriteria}</p>";
        return $"<p>{description}</p><h3>Acceptance criteria</h3><p>{acceptanceCriteria}</p>";
    }

    private static IReadOnlyList<string> SizeLabelNames(string size)
    {
        var label = size.ToLowerInvariant() switch
        {
            "s" => "size:s",
            "m" => "size:m",
            "l" => "size:l",
            _ => null
        };
        return label is null
            ? Array.Empty<string>()
            : new[] { label };
    }

    private static IReadOnlyList<ChildSpec>? TryExtractChildSpecs(IReadOnlyDictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("child_specs", out var raw)) return null;
        if (raw is not JsonElement je || je.ValueKind != JsonValueKind.Array) return null;

        var specs = new List<ChildSpec>();
        foreach (var el in je.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var title = GetStringFromElement(el, "title");
            var description = GetStringFromElement(el, "description");
            if (title is null || description is null) continue;

            var acceptanceCriteria = GetStringFromElement(el, "acceptance_criteria") ?? string.Empty;
            var size = GetStringFromElement(el, "size") ?? string.Empty;
            var scopeBoundary = GetStringFromElement(el, "scope_boundary") ?? string.Empty;

            specs.Add(new ChildSpec(title, description, acceptanceCriteria, size, scopeBoundary));
        }

        return specs;
    }

    private static string? GetStringFromElement(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
        return prop.ToString();
    }
}
