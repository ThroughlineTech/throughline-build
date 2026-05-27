using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs;

public static class ImplementBriefBuilder
{
    public static Brief Build(Ticket ticket, RepoState repo, string branchName, string worktreePath, ProjectContext? project = null, ReviewFeedback? reviewFeedback = null)
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

        var instruction = TemplateLoader.Load("implement.md").Substitute(vars);

        return new Brief(
            ticket.Id,
            Phase.Implement,
            instruction,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>
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
            });
    }

    private static string BuildReviewFeedbackSection(ReviewFeedback? reviewFeedback)
    {
        if (reviewFeedback is null)
            return "";

        var checksList = reviewFeedback.ChecksFailed.Count > 0
            ? string.Join("\n", reviewFeedback.ChecksFailed.Select(c => $"- {c}"))
            : "(none)";

        return $"## Rework round {reviewFeedback.ReworkRoundNumber} - reviewer feedback\n\n{reviewFeedback.Rationale}\n\nChecks failed:\n{checksList}";
    }
}
