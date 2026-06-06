using System.Text;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Briefs;

public static class BatchImplementBriefBuilder
{
    public static Brief Build(
        string agentName,
        IReadOnlyList<Ticket> tickets,
        RepoState repo,
        string branchName,
        string worktreePath,
        ChainCommitRange? chainCommitRange,
        ProjectContext? project = null)
    {
        if (tickets.Count == 0)
            throw new ArgumentException("Batch implement brief requires at least one ticket.", nameof(tickets));

        var proj = project ?? ProjectContext.Empty;
        var baseCommitSha = chainCommitRange?.EndSha ?? repo.MainSha;
        var chainPointer = BuildChainPointer(baseCommitSha, chainCommitRange);
        var ticketSections = BuildTicketSections(tickets);
        var declaredOrder = string.Join(", ", tickets.Select(t => t.Id));
        var workerResultJson = BuildWorkerResultJson(tickets);

        var vars = new Dictionary<string, string>
        {
            ["ticket_ids"] = declaredOrder,
            ["ticket_count"] = tickets.Count.ToString(),
            ["ticket_sections"] = ticketSections,
            ["declared_order"] = declaredOrder,
            ["worktree_path"] = worktreePath,
            ["branch"] = branchName,
            ["main_sha"] = repo.MainSha,
            ["base_commit_sha"] = baseCommitSha,
            ["chain_pointer"] = chainPointer,
            ["worker_result_json"] = workerResultJson
        };

        var instruction = TemplateLoader.Load(agentName, "batch-implement.md").Substitute(vars);

        var relevantFiles = chainCommitRange is not null && !chainCommitRange.IsEmpty
            ? Deduplicate(chainCommitRange.TouchedFiles)
            : Array.Empty<string>();

        var context = new Dictionary<string, string>
        {
            ["main_sha"] = repo.MainSha,
            ["base_commit_sha"] = baseCommitSha,
            ["branch"] = branchName,
            ["worktree_path"] = worktreePath,
            ["ticket_ids"] = declaredOrder,
            ["ticket_count"] = tickets.Count.ToString(),
            ["project_language"] = proj.Language,
            ["project_framework"] = proj.Framework,
            ["project_package_manager"] = proj.PackageManager,
            ["project_build_command"] = proj.BuildCommand,
            ["project_test_command"] = proj.TestCommand,
            ["project_install_command"] = proj.InstallCommand,
            ["project_dev_command"] = proj.DevCommand,
            ["project_plane_project_url"] = proj.PlaneProjectUrl,
            ["project_notes"] = proj.Notes,
            ["chain_pointer"] = chainPointer
        };

        return new Brief(
            declaredOrder,
            Phase.Implement,
            instruction,
            relevantFiles,
            Array.Empty<string>(),
            context);
    }

    private static string BuildTicketSections(IReadOnlyList<Ticket> tickets)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < tickets.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');

            var ticket = tickets[i];
            var relations = ticket.Relations.Count > 0
                ? string.Join("\n", ticket.Relations.Select(r => $"- {r.Kind}: {r.TargetId}"))
                : "(none)";
            var ordering = i == 0
                ? "First in this batch. Start from the base commit pointer."
                : $"Must be implemented after ticket {tickets[i - 1].Id} and on top of that ticket's commit.";

            AppendLine(builder, $"### {i + 1}. {ticket.Id} - {ticket.Title}");
            AppendLine(builder, $"- Type: {ticket.Type}");
            AppendLine(builder, $"- Size: {ticket.Size}");
            AppendLine(builder, $"- Risk: {ticket.Risk}");
            AppendLine(builder, $"- Stack position: {i + 1}");
            AppendLine(builder, $"- Ordering constraint: {ordering}");
            builder.Append('\n');
            AppendLine(builder, "Relations:");
            AppendLine(builder, relations);
            builder.Append('\n');
            AppendLine(builder, "Plan (raw HTML from ticket description; read directly, do not re-render):");
            AppendLine(builder, ticket.DescriptionHtml);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendLine(StringBuilder builder, string value)
    {
        builder.Append(value);
        builder.Append('\n');
    }

    private static string BuildChainPointer(string baseCommitSha, ChainCommitRange? chainCommitRange)
    {
        if (chainCommitRange is null)
            return $"current base commit pointer: {baseCommitSha}";

        return $"current base commit pointer: {baseCommitSha}; prior chain range: {chainCommitRange.StartSha}..{chainCommitRange.EndSha} ({chainCommitRange.CommitCount} commit(s))";
    }

    private static string BuildWorkerResultJson(IReadOnlyList<Ticket> tickets)
    {
        var ticketResults = string.Join(
            ",",
            tickets.Select((ticket, index) =>
                $"{{\"ticket_id\":\"{ticket.Id}\",\"commit_sha\":\"<commit SHA after {ticket.Id}>\"," +
                $"\"stack_position\":{index + 1},\"files_changed\":[\"path/relative/to/worktree\"]," +
                $"\"summary_ref\":\"IMPLEMENT_SUMMARY_{index + 1}\"}}"));

        return
            "{\"status\":\"Ok\",\"summary\":\"Implemented batch " +
            string.Join(", ", tickets.Select(t => t.Id)) +
            "\",\"files_changed\":[\"path/relative/to/worktree\"],\"failure_reason\":null," +
            "\"metadata\":{\"base_commit_sha\":\"<base commit pointer from brief>\"," +
            "\"head_commit_sha\":\"<HEAD SHA after all ticket commits>\"," +
            "\"files_changed\":[\"path/relative/to/worktree\"]}," +
            $"\"tickets\":[{ticketResults}]}}";
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
}
