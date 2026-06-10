using System.Text;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Briefs;

public sealed record ReworkBriefContext(
    string? ImplementSummary,
    IReadOnlyList<string> TouchedFiles);

public static class ImplementBriefBuilder
{
    private const int MaxReworkSummaryChars = 2000;

    public static Brief Build(
        string agentName,
        Ticket ticket,
        RepoState repo,
        string branchName,
        string worktreePath,
        ProjectContext? project = null,
        ReviewFeedback? reviewFeedback = null,
        ChainCommitRange? chainCommitRange = null,
        ReworkBriefContext? reworkContext = null,
        string preloadedContextSection = "")
    {
        var proj = project ?? ProjectContext.Empty;

        var relations = ticket.Relations.Count > 0
            ? string.Join("\n", ticket.Relations.Select(r => $"- {r.Kind}: {r.TargetId}"))
            : "(none)";

        var reviewFeedbackSection = BuildReviewFeedbackSection(reviewFeedback, reworkContext);
        var obsoleteDetectionSection = BuildObsoleteDetectionSection(reviewFeedback);

        var vars = new Dictionary<string, string>
        {
            ["ticket_id"] = ticket.Id,
            ["title"] = ticket.Title,
            ["type"] = ticket.Type,
            ["size"] = ticket.Size.ToString(),
            ["risk"] = ticket.Risk.ToString(),
            ["relations"] = relations,
            ["description_html"] = ticket.DescriptionHtml,
            ["worktree_path"] = worktreePath,
            ["branch"] = branchName,
            ["main_sha"] = repo.MainSha,
            ["review_feedback_section"] = reviewFeedbackSection,
            ["obsolete_detection_section"] = obsoleteDetectionSection,
            // Experiment 2: the pre-loaded-context block (named inputs + convention bundle), built by
            // the phase from the live worktree. Empty "" => the placeholder substitutes inert, leaving
            // the brief byte-identical to the pre-preload baseline (the ablation / review-reconstruct
            // case). The leading newline lives inside the section string (see PreloadedContextBuilder).
            ["preloaded_context_section"] = preloadedContextSection,
            // Experiment 4 (L2a): an effort-gated planning-hygiene bullet appended to Constraints, only
            // when [project].context_hygiene is on AND the brief is S-effort. Empty "" otherwise, which
            // the placeholder renders inert (off and M/L stay on the blessed empty-token baseline). The
            // leading newline lives inside the section string (same convention as preloaded_context_section).
            ["context_hygiene_section"] = BuildContextHygieneSection(proj, ticket)
        };

        var instruction = TemplateLoader.Load(agentName, "implement.md").Substitute(vars);

        // Fold pointer-only file hints into RelevantFiles (deduped).
        // Initial non-chain runs keep this empty; rework runs use prior touched files.
        IReadOnlyList<string> relevantFiles = Array.Empty<string>();
        string chainPointer = "";
        var relevantFileCandidates = new List<string>();
        if (chainCommitRange is not null && !chainCommitRange.IsEmpty)
        {
            relevantFileCandidates.AddRange(chainCommitRange.TouchedFiles);

            // Single bounded pointer line for Context: "chain prior commits: <start>..<end> (<N> commit(s))"
            chainPointer = $"chain prior commits: {chainCommitRange.StartSha}..{chainCommitRange.EndSha} ({chainCommitRange.CommitCount} commit(s))";
        }
        if (reviewFeedback is not null && reworkContext is not null)
            relevantFileCandidates.AddRange(reworkContext.TouchedFiles);
        if (relevantFileCandidates.Count > 0)
            relevantFiles = Deduplicate(relevantFileCandidates);

        var context = new Dictionary<string, string>
        {
            ["main_sha"] = repo.MainSha,
            ["branch"] = branchName,
            ["worktree_path"] = worktreePath,
            ["project_language"] = proj.Language,
            ["project_framework"] = proj.Framework,
            ["project_package_manager"] = proj.PackageManager,
            ["project_build_command"] = proj.BuildCommand,
            ["project_test_command"] = proj.TestCommand,
            ["project_install_command"] = proj.InstallCommand,
            ["project_dev_command"] = proj.DevCommand,
            ["project_plane_project_url"] = proj.PlaneProjectUrl,
            ["project_notes"] = proj.Notes,
            ["review_feedback_section"] = reviewFeedbackSection
        };

        // Only add chain_pointer when it is non-empty so an absent/empty range
        // leaves the Context identical to the no-pointer baseline.
        if (!string.IsNullOrEmpty(chainPointer))
            context["chain_pointer"] = chainPointer;

        return new Brief(
            ticket.Id,
            Phase.Implement,
            instruction,
            relevantFiles,
            Array.Empty<string>(),
            context);
    }

    private static string BuildReviewFeedbackSection(ReviewFeedback? reviewFeedback, ReworkBriefContext? reworkContext)
    {
        if (reviewFeedback is null)
            return "";

        var priorContextSection = BuildPriorImplementContextSection(reworkContext);

        if (reviewFeedback.GateFailedChecks is { Count: > 0 })
            return BuildGateFailureFeedbackSection(reviewFeedback, priorContextSection);

        var checksList = reviewFeedback.ChecksFailed.Count > 0
            ? string.Join("\n", reviewFeedback.ChecksFailed.Select(c => $"- {c}"))
            : "(none)";

        return $"\n## Rework round {reviewFeedback.ReworkRoundNumber} - reviewer feedback\n\n{reviewFeedback.Rationale}\n\nChecks failed:\n{checksList}{priorContextSection}";
    }

    private static string BuildGateFailureFeedbackSection(ReviewFeedback reviewFeedback, string priorContextSection)
    {
        var sb = new StringBuilder();
        sb.Append($"\n## Rework round {reviewFeedback.ReworkRoundNumber} - gate failure\n\n{reviewFeedback.Rationale}");
        foreach (var check in reviewFeedback.GateFailedChecks!)
        {
            sb.Append($"\n\n### Failed check: {check.Name} (exit {check.ExitCode})");
            if (!string.IsNullOrWhiteSpace(check.StdoutTail))
                sb.Append($"\n\nstdout:\n```\n{check.StdoutTail.TrimEnd()}\n```");
            if (!string.IsNullOrWhiteSpace(check.StderrTail))
                sb.Append($"\n\nstderr:\n```\n{check.StderrTail.TrimEnd()}\n```");
        }
        sb.Append(priorContextSection);
        return sb.ToString();
    }

    private static string BuildPriorImplementContextSection(ReworkBriefContext? reworkContext)
    {
        if (reworkContext is null)
            return "";

        var summary = string.IsNullOrWhiteSpace(reworkContext.ImplementSummary)
            ? "(not available)"
            : Bound(reworkContext.ImplementSummary.Trim(), MaxReworkSummaryChars);

        var files = Deduplicate(reworkContext.TouchedFiles);
        var filesList = files.Count > 0
            ? string.Join("\n", files.Select(f => $"- {f}"))
            : "(none found)";

        return $"\n\n## Prior implement context\n\nImplementer summary:\n{summary}\n\nTouched files from prior implement commit:\n{filesList}";
    }

    private static IReadOnlyList<string> Deduplicate(IEnumerable<string> files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var file in files)
        {
            if (!string.IsNullOrWhiteSpace(file) && seen.Add(file))
                result.Add(file);
        }
        return result.AsReadOnly();
    }

    private static string Bound(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;

        return value[..maxChars] + $"... [truncated: {value.Length - maxChars} more chars]";
    }

    // The obsolete path lets an agent escalate a ticket it finds already delivered by a
    // prior commit. On the INITIAL round that is a real signal (a sibling/earlier ticket
    // may have subsumed this one). On a REWORK round it is a trap: the rework agent reuses
    // the same branch, so the "prior commit that already satisfies the criteria" is its own
    // earlier-round work. Left enabled, the agent declares the ticket obsolete-subsumed by
    // its own commit and skips the reviewer's feedback (the TLB-424 chain-stop). So suppress
    // obsolete detection on rework rounds and redirect the agent to the feedback instead.
    private static string BuildObsoleteDetectionSection(ReviewFeedback? reviewFeedback)
    {
        // Both variants are agent-agnostic static prose (the rework case also embeds a full
        // escalate WORKER_RESULT example), so they live in shared templates with no placeholders.
        return reviewFeedback is not null
            ? TemplateLoader.LoadShared("implement-obsolete-rework.md")
            : TemplateLoader.LoadShared("implement-obsolete-initial.md");
    }

    // Experiment 4 (L2a): the planning-hygiene Constraints bullet, rendered only for S-effort briefs
    // when [project].context_hygiene is enabled. Stack-agnostic prose: no language, extension, or tool
    // name. The leading newline makes the section own its placement after the prior Constraints bullet;
    // empty string leaves the brief byte-identical to the flag-off baseline. Never rendered for M or L.
    private static string BuildContextHygieneSection(ProjectContext proj, Ticket ticket)
    {
        var lean = proj.ContextHygiene && ticket.Size == Size.S;
        if (!lean)
            return "";
        return "\n- Planning hygiene (this is a small, single-area brief): keep planning lightweight. Do not maintain an"
             + "\n  elaborate, continuously-rewritten task list for a change this focused. Do not re-read files whose contents"
             + "\n  are already provided to you above. Prefer targeted reads of the specific symbols you need over reading whole"
             + "\n  large files.";
    }
}
