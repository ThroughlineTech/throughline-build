using System.Text;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Builds an inspectable, role-specific worker brief without starting a worker.
/// Ticket reads, git reads, and one requested output write are the only effects.
/// </summary>
public static class WorkerBriefCommand
{
    private const string BriefSubcommand = "brief";

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        bool json,
        ITicketing ticketing,
        IGitClient git,
        ProjectContext project,
        IReadOnlyList<CheckSpec> checks,
        string targetBranch,
        string agentName,
        TextWriter output,
        TextWriter error,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? phaseAgents = null)
    {
        var parsed = Parse(args);
        if (parsed.Error is not null)
            return Usage(json, output, error, parsed.Error);

        string worktreePath;
        string outputPath;
        try
        {
            worktreePath = Path.GetFullPath(parsed.WorktreePath!);
            outputPath = Path.GetFullPath(parsed.OutputPath!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Failure(json, output, error, CliErrorCodes.Usage, ex.Message, 2);
        }

        if (!Directory.Exists(worktreePath))
            return Failure(json, output, error, CliErrorCodes.Failure,
                $"worktree path does not exist or is not a directory: {worktreePath}", 1);

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
            return Failure(json, output, error, CliErrorCodes.Failure,
                $"output directory does not exist: {outputDirectory ?? outputPath}", 1);

        Ticket ticket;
        try
        {
            ticket = await ticketing.GetAsync(parsed.TicketId!, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            return Failure(json, output, error, CliErrorCodes.NotFound, ex.Message, 1);
        }
        catch (ArgumentException ex)
        {
            return Failure(json, output, error, CliErrorCodes.Usage, ex.Message, 2);
        }
        catch (OperationCanceledException)
        {
            return Failure(json, output, error, CliErrorCodes.Failure, "cancelled", 1);
        }

        IReadOnlyList<TicketComment> comments = Array.Empty<TicketComment>();
        ReviewFeedback? reworkFeedback = null;
        if (parsed.Role == "rework")
        {
            try
            {
                comments = await ticketing.GetCommentsAsync(parsed.TicketId!, ct).ConfigureAwait(false);
                reworkFeedback = BuildReworkFeedback(comments);
            }
            catch (OperationCanceledException)
            {
                return Failure(json, output, error, CliErrorCodes.Failure, "cancelled", 1);
            }

            if (reworkFeedback is null)
            {
                return Failure(json, output, error, CliErrorCodes.Failure,
                    "rework requires a prior blocking review or gate finding", 1);
            }
        }

        BriefEvidence evidence;
        try
        {
            evidence = await ReadEvidenceAsync(
                git, worktreePath, targetBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(json, output, error, CliErrorCodes.Failure, "cancelled", 1);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Failure(json, output, error, CliErrorCodes.Failure, ex.Message, 1);
        }

        string instruction;
        try
        {
            var phaseName = parsed.Role == "rework" ? "implement" : parsed.Role!;
            var effectiveAgentName = phaseAgents is not null && phaseAgents.TryGetValue(phaseName, out var phaseAgent)
                ? phaseAgent
                : agentName;
            instruction = BuildRoleInstruction(
                parsed.Role!,
                effectiveAgentName,
                ticket,
                evidence,
                project,
                reworkFeedback,
                comments);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(json, output, error, CliErrorCodes.Failure, ex.Message, 1);
        }

        var gateCommand = $"build gate --ticket {ticket.Id} --role gating --json";
        var artifact = RenderArtifact(
            ticket,
            parsed.Role!,
            worktreePath,
            evidence,
            gateCommand,
            checks,
            instruction,
            reworkFeedback);

        try
        {
            File.WriteAllText(outputPath, artifact, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure(json, output, error, CliErrorCodes.Failure,
                $"could not write worker brief '{outputPath}': {ex.Message}", 1);
        }

        var view = new WorkerBriefView(
            SourceTicketId: ticket.Id,
            Role: parsed.Role!,
            OutputPath: outputPath,
            WorktreePath: worktreePath,
            Branch: evidence.Branch,
            BaseRef: evidence.BaseRef,
            BaseSha: evidence.BaseSha,
            HeadSha: evidence.HeadSha,
            GateCommand: gateCommand,
            ChangedPaths: evidence.Diff.Entries.Select(e => e.Path).ToList(),
            TrackedStatus: evidence.TrackedStatus,
            UntrackedPaths: evidence.UntrackedPaths);

        if (json)
            CliEnvelopeWriter.WriteWorkerBrief(output, view);
        else
        {
            output.WriteLine($"Worker brief written: {outputPath}");
            output.WriteLine($"source ticket: {ticket.Id}");
            output.WriteLine($"role: {parsed.Role}");
        }

        return 0;
    }

    private static async Task<BriefEvidence> ReadEvidenceAsync(
        IGitClient git,
        string worktreePath,
        string targetBranch,
        CancellationToken ct)
    {
        var branch = await git.CurrentBranchAsync(worktreePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(branch))
            throw new InvalidOperationException($"could not determine the current branch in {worktreePath}");

        var (baseRef, baseSha) = await BaseRefResolver.ResolveAsync(
            git, worktreePath, targetBranch, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseSha))
            throw new InvalidOperationException($"could not resolve base branch '{targetBranch}' in {worktreePath}");

        var headSha = await git.HeadShaAsync(worktreePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(headSha))
            throw new InvalidOperationException($"could not read HEAD in {worktreePath}");

        var tracked = await git.GetTrackedChangesResultAsync(worktreePath, ct).ConfigureAwait(false);
        if (!tracked.Success)
            throw new InvalidOperationException(
                $"could not read git status in {worktreePath}: {tracked.FailureReason ?? "unknown git error"}");

        var untracked = await git.GetUntrackedFilesResultAsync(worktreePath, ct).ConfigureAwait(false);
        if (!untracked.Success)
            throw new InvalidOperationException(
                $"could not read untracked files in {worktreePath}: {untracked.FailureReason ?? "unknown git error"}");

        var toRef = string.Equals(branch, "HEAD", StringComparison.Ordinal) ? "HEAD" : branch;
        var diff = await git.DiffAsync(
            baseRef, toRef, worktreePath, includePatchContent: true, ct).ConfigureAwait(false);

        var topLevelEntries = Directory.EnumerateFileSystemEntries(worktreePath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToList()
            .AsReadOnly();

        return new BriefEvidence(
            WorktreePath: worktreePath,
            BaseRef: baseRef,
            BaseSha: baseSha,
            HeadSha: headSha,
            Branch: branch,
            Diff: diff,
            TrackedStatus: tracked.Paths,
            UntrackedPaths: untracked.Paths,
            TopLevelEntries: topLevelEntries);
    }

    private static string BuildRoleInstruction(
        string role,
        string agentName,
        Ticket ticket,
        BriefEvidence evidence,
        ProjectContext project,
        ReviewFeedback? reworkFeedback,
        IReadOnlyList<TicketComment> comments)
    {
        var repo = new RepoState(evidence.BaseSha, evidence.TopLevelEntries);
        return role switch
        {
            "implement" => NormalizeConductorInstruction(ImplementBriefBuilder.Build(
                agentName,
                ticket,
                repo,
                evidence.Branch,
                evidence.WorktreePath,
                project).Instruction),
            "review" => NormalizeReviewInstruction(
                ReviewBriefBuilder.Build(
                    agentName,
                    ticket,
                    evidence.Diff,
                    NeutralImplementerResult(evidence),
                    Array.Empty<CheckResult>(),
                    project).Instruction),
            "rework" => NormalizeConductorInstruction(ImplementBriefBuilder.Build(
                agentName,
                ticket,
                repo,
                evidence.Branch,
                evidence.WorktreePath,
                project,
                reviewFeedback: reworkFeedback,
                reworkContext: new ReworkBriefContext(
                    LatestImplementSummary(comments),
                    evidence.Diff.Entries.Select(e => e.Path).ToList())).Instruction),
            _ => throw new InvalidOperationException($"unsupported worker brief role: {role}")
        };
    }

    private static string NormalizeConductorInstruction(string instruction) => instruction
        .Replace(
            "to the working tree, committing logical units to the feature branch.",
            "to the working tree, leaving all changes for the conductor to commit after review.",
            StringComparison.Ordinal)
        .Replace(
            "- Commit all changes locally on branch ",
            "- Do NOT stage or commit changes. The conductor commits from the primary worktree after independent review; current branch ",
            StringComparison.Ordinal)
        .Replace(
            "\"commit_sha\":\"<HEAD SHA of feature branch after all commits>\"",
            "\"commit_sha\":null",
            StringComparison.Ordinal)
        .Replace(
            "- metadata.commit_sha must be the HEAD SHA of the feature branch after all commits land",
            "- metadata.commit_sha must be null because the conductor, not this worker, creates the commit",
            StringComparison.Ordinal);

    private static WorkerResult NeutralImplementerResult(BriefEvidence evidence) => new(
        Status.Ok,
        "No implementer conclusion is supplied. The reviewer must form an independent verdict from the current diff, status, and checks.",
        evidence.Diff.Entries.Select(e => e.Path).ToList(),
        null,
        new Dictionary<string, object>());

    private static string NormalizeReviewInstruction(string instruction) => instruction.Replace(
        "(no automated checks configured)",
        "Checks were not run by build worker brief; run the exact gate command in the artifact.",
        StringComparison.Ordinal).Replace(
        "- You do not need a clean build to reach a verdict; the automated check results above already capture build/test status.",
        "- This artifact did not run automated checks. Run the exact gate command in the artifact before reaching a verdict; do not infer build/test status from this brief.",
        StringComparison.Ordinal);

    private static string RenderArtifact(
        Ticket ticket,
        string role,
        string worktreePath,
        BriefEvidence evidence,
        string gateCommand,
        IReadOnlyList<CheckSpec> checks,
        string instruction,
        ReviewFeedback? reworkFeedback)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Build worker brief");
        sb.AppendLine();
        sb.AppendLine("This is an inspectable artifact created by `build worker brief`. The command does not spawn a worker or mutate tickets, git history, branches, worktrees, or deployments.");
        sb.AppendLine();
        sb.AppendLine("## Brief metadata");
        sb.AppendLine();
        sb.AppendLine($"- Source ticket: `{ticket.Id}`");
        sb.AppendLine($"- Role: `{role}`");
        sb.AppendLine($"- Worktree path: `{worktreePath}`");
        sb.AppendLine($"- Branch: `{evidence.Branch}`");
        sb.AppendLine($"- Base ref: `{evidence.BaseRef}`");
        sb.AppendLine($"- Base SHA: `{evidence.BaseSha}`");
        sb.AppendLine($"- HEAD SHA: `{evidence.HeadSha}`");
        sb.AppendLine();

        sb.AppendLine("## Ticket body and acceptance criteria");
        sb.AppendLine();
        var description = HtmlToText.Render(ticket.DescriptionHtml);
        sb.AppendLine(string.IsNullOrWhiteSpace(description) ? "(empty)" : description.TrimEnd());
        sb.AppendLine();

        AppendSemanticContractGuidance(sb, role);

        sb.AppendLine("## Role boundaries");
        sb.AppendLine();
        switch (role)
        {
            case "implement":
                sb.AppendLine($"- Implement the ticket in `{worktreePath}` on branch `{evidence.Branch}`.");
                sb.AppendLine("- You may edit files, but do not stage or commit. The conductor creates the candidate commit after independent review.");
                sb.AppendLine("- Do not mutate the ticket, main branch, unrelated branches, other worktrees, or deployments.");
                break;
            case "review":
                sb.AppendLine("- Review independently from the current diff, worktree status, and configured checks.");
                sb.AppendLine("- Do not edit files, commit, stash, checkout, reset, rebase, create branches, alter worktrees, or mutate tickets.");
                sb.AppendLine("- No implementer conclusion is a verdict. Return a judgment grounded in the evidence you inspected.");
                break;
            case "rework":
                sb.AppendLine($"- Rework in the same supplied worktree `{worktreePath}` and keep branch `{evidence.Branch}`.");
                sb.AppendLine("- Fix only the blocking findings, do not stage or commit, and rerun the exact gate command.");
                sb.AppendLine("- Do not create or switch branches or worktrees, and do not mutate the ticket or deployment.");
                break;
        }
        sb.AppendLine();

        sb.AppendLine("## Exact gate command");
        sb.AppendLine();
        sb.AppendLine("Run from the worktree path above:");
        sb.AppendLine();
        sb.AppendLine($"`{gateCommand}`");
        sb.AppendLine();
        sb.AppendLine("Configured checks (not run by this artifact command):");
        var gateChecks = checks
            .Where(check => check.Role is CheckRole.Setup or CheckRole.Gating)
            .ToList();
        if (gateChecks.Count == 0)
            sb.AppendLine("- (none configured)");
        else
        {
            foreach (var check in gateChecks)
                sb.AppendLine($"- {check.Name}: `{AutomatedChecksRunner.FormatCommandLine(check)}`");
        }
        sb.AppendLine();

        sb.AppendLine("## Declared surface and write fence");
        sb.AppendLine();
        sb.AppendLine("No explicit surface or fence was supplied to this command. Use the ticket scope and role boundaries above as the fence.");
        sb.AppendLine();

        AppendEvidence(sb, evidence);

        if (reworkFeedback is not null)
        {
            sb.AppendLine("## Prior blocking findings");
            sb.AppendLine();
            sb.AppendLine(reworkFeedback.Rationale.TrimEnd());
            if (reworkFeedback.ChecksFailed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Blocking checks:");
                foreach (var check in reworkFeedback.ChecksFailed)
                    sb.AppendLine($"- {check}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Generated role brief");
        sb.AppendLine();
        sb.AppendLine(instruction.TrimEnd());
        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendSemanticContractGuidance(StringBuilder sb, string role)
    {
        sb.AppendLine("## Semantic execution contract");
        sb.AppendLine();
        sb.AppendLine("The ticket body above carries a recorded `Ticket execution contract` when the conductor classifies the ticket as semantic-risk. Treat a recorded contract as binding; do not silently reinterpret it.");
        switch (role)
        {
            case "implement":
                sb.AppendLine("- If the ticket is marked semantic-risk and its execution contract is missing, ambiguous, or conflicts with inspected authority, stop before code edits and report the conflict to the conductor.");
                sb.AppendLine("- Before broadening a shared surface, public contract, or user-facing behavior, add the focused negative tests the contract requires when applicable.");
                break;
            case "review":
                sb.AppendLine("- Verify declared authority, forbidden shortcuts, required negative tests, and declared shared surfaces against the diff.");
                sb.AppendLine("- Report a clear-contract violation as an implementation defect. Report a missing, ambiguous, or conflicting contract as a plan or contract defect for the conductor, not as speculative implementation rework.");
                break;
            case "rework":
                sb.AppendLine("- Address only the numbered reviewer findings. Do not reinterpret the ticket execution contract; a changed contract requires a conductor amendment before more implementation.");
                sb.AppendLine("- If a semantic finding repeats after rework, stop for direct conductor inspection or escalation instead of starting another generic rework round.");
                break;
        }
        sb.AppendLine();
    }

    private static void AppendEvidence(StringBuilder sb, BriefEvidence evidence)
    {
        sb.AppendLine("## Current worktree evidence");
        sb.AppendLine();
        sb.AppendLine("The following values were read from the supplied worktree. They are evidence for the worker to inspect, not a verdict.");
        sb.AppendLine();
        sb.AppendLine("Committed changed paths from the resolved base:");
        if (evidence.Diff.Entries.Count == 0)
            sb.AppendLine("- (none)");
        else
        {
            foreach (var entry in evidence.Diff.Entries)
                sb.AppendLine($"- {entry.Path}");
        }

        sb.AppendLine();
        sb.AppendLine("Tracked worktree status:");
        if (evidence.TrackedStatus.Count == 0)
            sb.AppendLine("- (clean)");
        else
        {
            foreach (var path in evidence.TrackedStatus)
                sb.AppendLine($"- `{path}`");
        }

        sb.AppendLine();
        sb.AppendLine("Untracked worktree paths:");
        if (evidence.UntrackedPaths.Count == 0)
            sb.AppendLine("- (none)");
        else
        {
            foreach (var path in evidence.UntrackedPaths)
                sb.AppendLine($"- `{path}`");
        }
        sb.AppendLine();
    }

    private static ReviewFeedback? BuildReworkFeedback(IReadOnlyList<TicketComment> comments)
    {
        var findings = comments
            .Select(comment => new
            {
                Comment = comment,
                Text = HtmlToText.Render(comment.Body)
            })
            .Where(item => IsBlockingFindingMarker(item.Text))
            .OrderBy(item => item.Comment.CreatedAt)
            .ToList();

        if (findings.Count == 0)
            return null;

        var latest = findings[^1];
        return new ReviewFeedback(
            latest.Text.Trim(),
            ParseFailedChecks(latest.Text),
            findings.Count + 1);
    }

    private static bool IsBlockingFindingMarker(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("reviewed: rework", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("reviewed: fail", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("[gate:", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseFailedChecks(string text)
    {
        var marker = "checks_failed:";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return Array.Empty<string>();

        var values = text[(index + marker.Length)..]
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().TrimEnd('.', ';'))
            .Where(value => value.Length > 0)
            .ToList();
        return values.AsReadOnly();
    }

    private static string? LatestImplementSummary(IReadOnlyList<TicketComment> comments) => comments
        .Select(comment => new { Comment = comment, Text = HtmlToText.Render(comment.Body) })
        .Where(item => item.Text.Contains("[implemented_at:", StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.Comment.CreatedAt)
        .LastOrDefault()?.Text;

    private static ParsedArgs Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !string.Equals(args[1], BriefSubcommand, StringComparison.Ordinal))
            return new(null, null, null, null, "worker brief subcommand is required");

        string? ticketId = null;
        string? role = null;
        string? worktreePath = null;
        string? outputPath = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Count; i++)
        {
            var option = args[i];
            if (option is not "--ticket" and not "--role" and not "--worktree" and not "--output")
                return new(ticketId, role, worktreePath, outputPath, $"unknown option for worker brief: {option}");
            if (!seen.Add(option))
                return new(ticketId, role, worktreePath, outputPath, $"option may be specified only once: {option}");
            if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                return new(ticketId, role, worktreePath, outputPath, $"{option} requires a value");

            switch (option)
            {
                case "--ticket": ticketId = args[i]; break;
                case "--role": role = args[i].ToLowerInvariant(); break;
                case "--worktree": worktreePath = args[i]; break;
                case "--output": outputPath = args[i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(ticketId))
            return new(ticketId, role, worktreePath, outputPath, "--ticket is required");
        if (role is not ("implement" or "review" or "rework"))
            return new(ticketId, role, worktreePath, outputPath, "--role must be implement, review, or rework");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return new(ticketId, role, worktreePath, outputPath, "--worktree is required");
        if (string.IsNullOrWhiteSpace(outputPath))
            return new(ticketId, role, worktreePath, outputPath, "--output is required");

        return new(ticketId, role, worktreePath, outputPath, null);
    }

    private static int Usage(bool json, TextWriter output, TextWriter error, string message) => Failure(
        json,
        output,
        error,
        CliErrorCodes.Usage,
        message + ". Usage: build worker brief --ticket <id> --role <implement|review|rework> --worktree <path> --output <path> [--json]",
        2);

    private static int Failure(
        bool json,
        TextWriter output,
        TextWriter error,
        string code,
        string message,
        int exitCode)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, code, message);
        else
            error.WriteLine($"Error: {message}");
        return exitCode;
    }

    private sealed record ParsedArgs(
        string? TicketId,
        string? Role,
        string? WorktreePath,
        string? OutputPath,
        string? Error);

    private sealed record BriefEvidence(
        string WorktreePath,
        string BaseRef,
        string BaseSha,
        string HeadSha,
        string Branch,
        GitDiff Diff,
        IReadOnlyList<string> TrackedStatus,
        IReadOnlyList<string> UntrackedPaths,
        IReadOnlyList<string> TopLevelEntries);
}
