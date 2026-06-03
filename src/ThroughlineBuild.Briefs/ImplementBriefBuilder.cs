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
            ["worker_result_json"] = workerResultJson,
            ["review_feedback_section"] = reviewFeedbackSection,
            ["obsolete_detection_section"] = obsoleteDetectionSection
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

    // The obsolete path lets an agent escalate a ticket it finds already delivered by a
    // prior commit. On the INITIAL round that is a real signal (a sibling/earlier ticket
    // may have subsumed this one). On a REWORK round it is a trap: the rework agent reuses
    // the same branch, so the "prior commit that already satisfies the criteria" is its own
    // earlier-round work. Left enabled, the agent declares the ticket obsolete-subsumed by
    // its own commit and skips the reviewer's feedback (the TLB-424 chain-stop). So suppress
    // obsolete detection on rework rounds and redirect the agent to the feedback instead.
    private static string BuildObsoleteDetectionSection(ReviewFeedback? reviewFeedback)
    {
        if (reviewFeedback is not null)
        {
            return
                "## Obsolete detection (suppressed on rework)\n\n" +
                "This is a rework round. Commits already on this branch are your own prior-round work - " +
                "do NOT raise an obsolete escalation against them. Apply the reviewer feedback above. " +
                "If the feedback is genuinely already satisfied by the current tree, re-affirm with " +
                "`Status=Ok` and report the current HEAD SHA; never emit `Status=Escalate` with reason " +
                "`obsolete` in a rework round.";
        }

        return
            "## Obsolete detection\n\n" +
            "Before making any changes, check whether the plan's acceptance criteria are already satisfied by a prior commit. If the acceptance criteria's artifacts already exist AND their content meets the acceptance criteria, the ticket is obsolete.\n\n" +
            "**Detection bar:** \"the file exists AND its content meets the acceptance criteria\" qualifies. \"a file with the same name exists\" does not.\n\n" +
            "Emit `Status=Escalate` with a populated `metadata.escalation` block. Do not make any changes.\n\n" +
            "WORKER_RESULT\n" +
            "{\n" +
            "  \"status\": \"Escalate\",\n" +
            "  \"summary\": \"Ticket obsolete: decompose.md already delivered in commit 80ccafa\",\n" +
            "  \"files_changed\": [],\n" +
            "  \"failure_reason\": null,\n" +
            "  \"metadata\": {\n" +
            "    \"escalation\": {\n" +
            "      \"reason\": \"obsolete\",\n" +
            "      \"subsumed_by\": {\n" +
            "        \"commit\": \"80ccafa\",\n" +
            "        \"files\": [\"src/ThroughlineBuild.Briefs/Templates/claude-code/decompose.md\"],\n" +
            "        \"rationale\": \"decompose.md delivered in commit 80ccafa; file meets this brief's acceptance criteria\"\n" +
            "      }\n" +
            "    }\n" +
            "  }\n" +
            "}";
    }
}
