using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Briefs;

public static class ImplementBriefBuilder
{
    public static Brief Build(
        string agentName,
        Ticket ticket,
        RepoState repo,
        string branchName,
        string worktreePath,
        ProjectContext? project = null,
        ReviewFeedback? reviewFeedback = null,
        ChainCommitRange? chainCommitRange = null)
    {
        var proj = project ?? ProjectContext.Empty;

        var relations = ticket.Relations.Count > 0
            ? string.Join("\n", ticket.Relations.Select(r => $"- {r.Kind}: {r.TargetId}"))
            : "(none)";

        var workerResultJson =
            $"{{\"status\":\"Ok\",\"summary\":\"Implemented {ticket.Id}\"," +
            $"\"files_changed\":[\"path/relative/to/worktree\"],\"failure_reason\":null," +
            $"\"metadata\":{{\"commit_sha\":\"<HEAD SHA of feature branch after all commits>\"," +
            $"\"files_changed\":[\"path/relative/to/worktree\"]}}}}";

        var reviewFeedbackSection = BuildReviewFeedbackSection(reviewFeedback);

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
            ["worker_result_json"] = workerResultJson,
            ["review_feedback_section"] = reviewFeedbackSection
        };

        var instruction = TemplateLoader.Load(agentName, "implement.md").Substitute(vars);

        // Fold chain-handoff touched-files into RelevantFiles (deduped).
        // When the range is empty (first ticket) or null, RelevantFiles stays empty.
        IReadOnlyList<string> relevantFiles = Array.Empty<string>();
        string chainPointer = "";
        if (chainCommitRange is not null && !chainCommitRange.IsEmpty)
        {
            // Deduplicate while preserving first-encounter order.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var files = new List<string>();
            foreach (var f in chainCommitRange.TouchedFiles)
            {
                if (seen.Add(f))
                    files.Add(f);
            }
            relevantFiles = files.AsReadOnly();

            // Single bounded pointer line for Context: "chain prior commits: <start>..<end> (<N> commit(s))"
            chainPointer = $"chain prior commits: {chainCommitRange.StartSha}..{chainCommitRange.EndSha} ({chainCommitRange.CommitCount} commit(s))";
        }

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

    private static string BuildReviewFeedbackSection(ReviewFeedback? reviewFeedback)
    {
        if (reviewFeedback is null)
            return "";

        var checksList = reviewFeedback.ChecksFailed.Count > 0
            ? string.Join("\n", reviewFeedback.ChecksFailed.Select(c => $"- {c}"))
            : "(none)";

        return $"\n## Rework round {reviewFeedback.ReworkRoundNumber} - reviewer feedback\n\n{reviewFeedback.Rationale}\n\nChecks failed:\n{checksList}";
    }
}
