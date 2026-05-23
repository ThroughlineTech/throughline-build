using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs;

public static class ImplementBriefBuilder
{
    public static Brief Build(Ticket ticket, RepoState repo, string branchName, string worktreePath)
    {
        var relations = ticket.Relations.Count > 0
            ? string.Join("\n", ticket.Relations.Select(r => $"- {r.Kind}: {r.TargetId}"))
            : "(none)";

        var workerResultJson =
            $"{{\"status\":\"Ok\",\"summary\":\"Implemented {ticket.Id}\"," +
            $"\"filesChanged\":[\"path/relative/to/worktree\"],\"failureReason\":null," +
            $"\"metadata\":{{\"commit_sha\":\"<HEAD SHA of feature branch after all commits>\"," +
            $"\"files_changed\":[\"path/relative/to/worktree\"]}}}}";

        var instruction =
            $"# Implement Phase Brief\n\n" +
            $"You are an implementing agent. Your job is to apply the plan recorded in the ticket description to the working tree, committing logical units to the feature branch.\n\n" +
            $"## Ticket\n" +
            $"- Id: {ticket.Id}\n" +
            $"- Title: {ticket.Title}\n" +
            $"- Type: {ticket.Type}\n" +
            $"- Size: {ticket.Size}\n" +
            $"- Risk: {ticket.Risk}\n\n" +
            $"## Relations\n{relations}\n\n" +
            $"## Plan (from ticket description)\n" +
            $"The ticket description (raw HTML) contains the planning output from the prior phase. Read it directly; do not re-render.\n\n" +
            $"{ticket.DescriptionHtml}\n\n" +
            $"## Worktree and branch\n" +
            $"- Worktree path: {worktreePath}\n" +
            $"- Branch: {branchName}\n" +
            $"- Base SHA (origin/main): {repo.MainSha}\n\n" +
            $"## Constraints\n" +
            $"- Work only inside the worktree at {worktreePath}\n" +
            $"- Commit all changes locally on branch {branchName}\n" +
            $"- Do NOT force-push, do NOT rebase, do NOT mutate the main branch\n" +
            $"- Do NOT write outside the worktree\n\n" +
            $"## Required output\n" +
            $"Emit exactly one WORKER_RESULT block at the end of your response:\n\n" +
            $"WORKER_RESULT\n{workerResultJson}\n\n" +
            $"- metadata.commit_sha must be the HEAD SHA of the feature branch after all commits land\n" +
            $"- metadata.files_changed must be the list of paths (relative to the worktree root) you wrote";

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
                ["worktree_path"] = worktreePath
            });
    }
}
