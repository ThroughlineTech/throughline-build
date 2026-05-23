using System.Diagnostics;
using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

public record BuildOptions(
    string SessionId,
    string WorkerName,
    TimeSpan WorkerTimeout,
    IReadOnlyList<string>? WorkerAllowedTools = null);

public record PlanResult(
    bool Success,
    string TicketId,
    string? RiskLabel,
    string? SizeLabel,
    string? PlannedAtSha,
    string? FailureReason);

public sealed class ProcessGitClient : IGitClient
{
    private readonly string? _workingDirectory;

    public ProcessGitClient(string? workingDirectory = null)
    {
        _workingDirectory = workingDirectory;
    }

    public async Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", $"rev-parse {refspec}")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"git rev-parse {refspec} failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }
        return stdout.Trim();
    }

    public async Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct)
    {
        var wd = _workingDirectory ?? Directory.GetCurrentDirectory();
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = wd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--porcelain");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"git worktree list failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }
        return ParseWorktreeList(stdout);
    }

    private static IReadOnlyList<WorktreeInfo> ParseWorktreeList(string output)
    {
        var result = new List<WorktreeInfo>();
        string? currentPath = null;
        string? currentSha = null;
        string? currentBranch = null;
        bool isLocked = false;
        bool isPrunable = false;

        void FlushCurrent()
        {
            if (currentPath is not null)
            {
                result.Add(new WorktreeInfo(
                    currentPath,
                    currentBranch ?? "",
                    currentSha ?? "",
                    isLocked,
                    isPrunable));
                currentPath = null;
                currentSha = null;
                currentBranch = null;
                isLocked = false;
                isPrunable = false;
            }
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                FlushCurrent();
                continue;
            }
            if (line.StartsWith("worktree "))
                currentPath = line.Substring("worktree ".Length);
            else if (line.StartsWith("HEAD "))
                currentSha = line.Substring("HEAD ".Length);
            else if (line.StartsWith("branch refs/heads/"))
                currentBranch = line.Substring("branch refs/heads/".Length);
            else if (line == "detached")
                currentBranch = "";
            else if (line.StartsWith("locked"))
                isLocked = true;
            else if (line.StartsWith("prunable"))
                isPrunable = true;
        }

        FlushCurrent();
        return result;
    }

    public async Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
    {
        var wd = _workingDirectory ?? Directory.GetCurrentDirectory();
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = wd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("remove");
        if (force)
            psi.ArgumentList.Add("--force");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            return new WorktreeRemoveResult(false, stderr.Trim());
        return new WorktreeRemoveResult(true, null);
    }
}

public class PlanPhase
{
    private readonly ITicketing _ticketing;
    private readonly IWorkerAgent _worker;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly IGitClient _git;

    public PlanPhase(
        ITicketing ticketing,
        IWorkerAgent worker,
        IEventSink events,
        BuildOptions options,
        IGitClient? gitClient = null)
    {
        _ticketing = ticketing;
        _worker = worker;
        _events = events;
        _options = options;
        _git = gitClient ?? new ProcessGitClient();
    }

    public async Task<PlanResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        if (ticket.State != TicketState.Backlog)
            return new PlanResult(false, ticketId, null, null, null, "ticket not in Backlog state");

        string mainSha;
        try
        {
            mainSha = await _git.RevParseAsync("origin/main", workingDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PlanResult(false, ticketId, null, null, null, $"git rev-parse failed: {ex.Message}");
        }

        var topLevelEntries = Directory.EnumerateFileSystemEntries(workingDirectory)
            .ToList()
            .AsReadOnly();

        var repoState = new RepoState(mainSha, topLevelEntries);
        var brief = PlanBriefBuilder.Build(ticket, repoState);

        await EmitAsync(EventKind.WorkerSpawn, ticketId, new Dictionary<string, object>
        {
            ["worker"] = _options.WorkerName
        }, ct).ConfigureAwait(false);

        var workerOptions = new WorkerOptions(_options.WorkerTimeout, _options.WorkerAllowedTools);
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
            var llmData = FlattenLlmUsage(usageObj);
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

        await _ticketing.ApplyLabelsAsync(ticketId, new[] { $"risk:{riskLabel}", $"size:{sizeLabel}" }, ct).ConfigureAwait(false);
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

    private static IReadOnlyDictionary<string, object>? FlattenLlmUsage(object usageObj)
    {
        var result = new Dictionary<string, object>();

        // Handle IDictionary path (from unit tests or direct construction)
        if (usageObj is IDictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Value is not null)
                {
                    result[kvp.Key] = UnwrapJsonElement(kvp.Value);
                }
            }
            return result;
        }

        // Handle JsonElement path (from envelope round-trip)
        if (usageObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in je.EnumerateObject())
            {
                result[prop.Name] = UnwrapJsonElement(prop.Value);
            }
            return result;
        }

        // Neither dictionary nor JsonElement object - skip silently
        return null;
    }

    private static object UnwrapJsonElement(object value)
    {
        if (value is not JsonElement je)
            return value;

        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString() ?? "",
            JsonValueKind.Number => je.TryGetInt32(out var intVal) ? intVal : je.GetInt64(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => (object?)null ?? "",
            _ => value // Return as-is for Object, Array, etc.
        };
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
