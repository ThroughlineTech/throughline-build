using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

public record BuildOptions(
    string SessionId,
    string WorkerName,
    TimeSpan WorkerTimeout,
    IReadOnlyList<string>? WorkerAllowedTools = null,
    string? DebugCaptureDirectory = null,
    System.IO.TextWriter? LiveStdoutSink = null,
    System.IO.TextWriter? LiveStderrSink = null,
    System.IO.TextWriter? ProgressDigestSink = null);

public record PlanResult(
    bool Success,
    string TicketId,
    string? RiskLabel,
    string? SizeLabel,
    string? PlannedAtSha,
    string? FailureReason);

public class PlanPhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IWorkerAgent _worker;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly IGitClient _git;
    private readonly ProjectContext _project;

    public PlanPhase(
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

    public Phase Phase => Phase.Plan;

    public async Task<PlanResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        if (ticket.State != TicketState.Backlog)
            return new PlanResult(false, ticketId, null, null, null, "ticket not in Backlog state");

        string mainSha;
        try
        {
            (_, mainSha) = await BaseRefResolver.ResolveAsync(_git, workingDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PlanResult(false, ticketId, null, null, null, $"git rev-parse failed: {ex.Message}");
        }

        var topLevelEntries = Directory.EnumerateFileSystemEntries(workingDirectory)
            .ToList()
            .AsReadOnly();

        var repoState = new RepoState(mainSha, topLevelEntries);
        var brief = PlanBriefBuilder.Build(_worker.Name, ticket, repoState, _project);

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

        await _ticketing.TransitionAsync(ticketId, TicketState.Planning, ct).ConfigureAwait(false);

        await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
        {
            ["status"] = workerResult.Status.ToString()
        }, ct).ConfigureAwait(false);

        if (workerResult.Status != Status.Ok)
            return new PlanResult(false, ticketId, null, null, null,
                workerResult.FailureReason ?? workerResult.Status.ToString());

        if (workerResult.Metadata.TryGetValue("llm_usage", out var usageObj))
        {
            var llmData = LlmUsageFlattener.Flatten(usageObj);
            if (llmData is not null)
            {
                await EmitAsync(EventKind.LlmCall, ticketId, llmData, ct).ConfigureAwait(false);
            }
        }

        var (planHtml, riskLabel, sizeLabel, plannedAtSha) = TryExtractMetadata(workerResult.Metadata);
        if (planHtml is null || riskLabel is null || sizeLabel is null || plannedAtSha is null)
            return new PlanResult(false, ticketId, null, null, null,
                "worker metadata missing required keys (plan_html, risk_label, size_label, planned_at_sha)");

        await _ticketing.AppendDescriptionAsync(ticketId, planHtml, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "append_description"
        }, ct).ConfigureAwait(false);

        var mergedLabels = MergeRiskSizeLabels(ticket.Labels, riskLabel, sizeLabel);
        await _ticketing.ApplyLabelsAsync(ticketId, mergedLabels, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "apply_labels"
        }, ct).ConfigureAwait(false);

        await _ticketing.CreateCommentAsync(ticketId, $"<p>[planned_at: {plannedAtSha}]</p>", ct).ConfigureAwait(false);
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "create_comment"
        }, ct).ConfigureAwait(false);

        await _ticketing.TransitionAsync(ticketId, TicketState.Ready, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
        {
            ["from"] = "Backlog",
            ["to"] = "Ready"
        }, ct).ConfigureAwait(false);

        return new PlanResult(true, ticketId, riskLabel, sizeLabel, plannedAtSha, null);
    }

    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var planResult = await RunAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        var outputs = planResult.Success
            ? new Dictionary<string, string>
            {
                ["risk_label"] = planResult.RiskLabel!,
                ["size_label"] = planResult.SizeLabel!,
                ["planned_at_sha"] = planResult.PlannedAtSha!
            } as IReadOnlyDictionary<string, string>
            : new Dictionary<string, string>() as IReadOnlyDictionary<string, string>;
        return new PhaseResult(planResult.Success, planResult.TicketId, Phase.Plan, planResult.FailureReason, outputs);
    }

    private async Task EmitAsync(EventKind kind, string ticketId, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            ticketId,
            Phase.Plan,
            data), ct).ConfigureAwait(false);
    }


    private static IReadOnlyList<string> MergeRiskSizeLabels(
        IReadOnlyList<string> existing, string riskLabel, string sizeLabel)
    {
        var filtered = existing
            .Where(l => !l.StartsWith("risk:", StringComparison.OrdinalIgnoreCase)
                     && !l.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        filtered.Add($"risk:{riskLabel}");
        filtered.Add($"size:{sizeLabel}");
        return filtered;
    }

    private static (string? planHtml, string? riskLabel, string? sizeLabel, string? plannedAtSha)
        TryExtractMetadata(IReadOnlyDictionary<string, object> metadata)
    {
        static string? GetString(IReadOnlyDictionary<string, object> meta, string key)
        {
            if (!meta.TryGetValue(key, out var val)) return null;
            if (val is string s) return s;
            if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString();
            return val?.ToString();
        }

        return (
            GetString(metadata, "plan_html"),
            GetString(metadata, "risk_label"),
            GetString(metadata, "size_label"),
            GetString(metadata, "planned_at_sha")
        );
    }
}
