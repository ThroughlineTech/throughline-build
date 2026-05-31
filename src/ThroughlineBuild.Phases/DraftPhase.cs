using System.Text.Json;
using System.Text.RegularExpressions;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Phases;

/// <summary>
/// DraftPhase takes free-form operator text, invokes a worker subprocess via IWorkerAgent,
/// parses the WORKER_RESULT envelope's body_markdown field, validates minimal structure,
/// and returns a DraftResult. No Plane writes occur here - the caller sequences to NewPhase.
/// </summary>
public class DraftPhase : IWorkflowPhase
{
    private readonly IWorkerAgent _worker;
    private readonly BuildOptions _options;

    public DraftPhase(IWorkerAgent worker, BuildOptions options)
    {
        _worker = worker;
        _options = options;
    }

    public Phase Phase => Phase.Draft;

    /// <summary>
    /// Runs the draft phase for the supplied options.
    /// </summary>
    /// <param name="options">Draft options including operator text and debug flag.</param>
    /// <param name="workingDirectory">Directory passed to the worker subprocess.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A DraftResult carrying Outcome=Ok with BodyMarkdown on success, or a typed failure.</returns>
    public async Task<DraftResult> RunAsync(DraftPhaseOptions options, string workingDirectory, CancellationToken ct)
    {
        // Step 1: Validate non-empty operator text.
        if (string.IsNullOrWhiteSpace(options.OperatorText))
            return new DraftResult(DraftOutcome.EmptyInput, null, "Operator text must not be empty or whitespace.");

        // Step 2: Build the drafter brief.
        var brief = new Brief(
            TicketId: "",
            Phase: Phase.Draft,
            Instruction: DraftBriefBuilder.Build(_worker.Name, options.OperatorText),
            RelevantFiles: Array.Empty<string>(),
            AllowedWrites: Array.Empty<string>(),
            Context: new Dictionary<string, string>());

        // Step 3: Invoke the worker.
        string? debugDir = null;
        if (options.Debug && _options.DebugCaptureDirectory is not null)
            debugDir = _options.DebugCaptureDirectory;

        var workerOptions = new WorkerOptions(
            _options.WorkerTimeout,
            _options.WorkerAllowedTools,
            DebugCaptureDirectory: debugDir,
            LiveStdoutSink: _options.LiveStdoutSink,
            LiveStderrSink: _options.LiveStderrSink,
            ProgressDigestSink: _options.ProgressDigestSink);

        var workerResult = await _worker.ExecuteAsync(brief, workingDirectory, workerOptions, ct).ConfigureAwait(false);

        // Step 4: Map non-Ok worker status to WorkerFailed.
        if (workerResult.Status != Status.Ok)
            return new DraftResult(DraftOutcome.WorkerFailed, null,
                workerResult.FailureReason ?? workerResult.Status.ToString());

        // Step 5: Resolve body_markdown_ref from fenced block (primary), or fall back to body_markdown (legacy).
        string? bodyMarkdown;
        if (!FencedBlockResolver.TryResolveRef(
            workerResult.Blocks ?? new Dictionary<string, string>(),
            workerResult.Metadata,
            "body_markdown_ref",
            out bodyMarkdown,
            out var blockError))
        {
            // Fallback: try direct body_markdown key (backward compat)
            bodyMarkdown = GetString(workerResult.Metadata, "body_markdown");
            if (bodyMarkdown is null)
                return new DraftResult(DraftOutcome.InvalidWorkerOutput, null,
                    "Worker metadata missing body_markdown_ref (fenced block) and body_markdown (legacy): " + (blockError ?? "no block found"));
        }

        // Step 6: Validate minimal required sections (loose v1: title + non-empty description).
        var missing = ValidateSections(bodyMarkdown);
        if (missing.Count > 0)
            return new DraftResult(DraftOutcome.InvalidWorkerOutput, null,
                $"Worker output missing required sections: {string.Join(", ", missing)}");

        // Step 7: Return success.
        return new DraftResult(DraftOutcome.Ok, bodyMarkdown, null);
    }

    /// <summary>
    /// Explicit IWorkflowPhase implementation.
    /// DraftPhase does not operate on existing tickets; call the typed RunAsync directly.
    /// </summary>
    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        throw new NotSupportedException(
            "DraftPhase produces a new ticket body from operator text and does not support the ticketId-based dispatch pattern. " +
            "Call RunAsync(DraftPhaseOptions, workingDirectory, CancellationToken) directly.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string? GetString(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString();
        return val?.ToString();
    }

    /// <summary>
    /// Validates that the body markdown contains at minimum a title and non-empty description.
    /// Returns a list of missing element names; empty list means valid.
    /// </summary>
    private static IReadOnlyList<string> ValidateSections(string bodyMarkdown)
    {
        var missing = new List<string>();

        // Title: ATX heading "# ..." or "**Title:**" marker.
        bool hasTitle = Regex.IsMatch(bodyMarkdown, @"(^|\n)\s*#\s+\S", RegexOptions.Multiline)
            || Regex.IsMatch(bodyMarkdown, @"\*\*Title:\*\*\s*\S", RegexOptions.IgnoreCase);

        if (!hasTitle)
            missing.Add("title");

        // Description: body must have non-whitespace content beyond the title line.
        var lines = bodyMarkdown.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        bool foundTitle = false;
        bool hasDescriptionBody = false;
        foreach (var line in lines)
        {
            if (!foundTitle && (line.TrimStart().StartsWith("# ", StringComparison.Ordinal)
                || Regex.IsMatch(line, @"\*\*Title:\*\*", RegexOptions.IgnoreCase)))
            {
                foundTitle = true;
                continue;
            }
            if (foundTitle && !string.IsNullOrWhiteSpace(line))
            {
                hasDescriptionBody = true;
                break;
            }
        }

        if (!hasDescriptionBody)
            missing.Add("description body");

        return missing;
    }
}
