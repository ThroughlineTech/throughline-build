using System.Text.RegularExpressions;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs;

public record RepoState(string MainSha, IReadOnlyList<string> TopLevelEntries);

public static class PlanBriefBuilder
{
    public static Brief Build(Ticket ticket, RepoState repo, ProjectContext? project = null)
    {
        var proj = project ?? ProjectContext.Empty;

        var relations = ticket.Relations.Count > 0
            ? string.Join("\n", ticket.Relations.Select(r => $"- {r.Kind}: {r.TargetId}"))
            : "(none)";

        var topLevelEntries = repo.TopLevelEntries.Count > 0
            ? string.Join("\n", repo.TopLevelEntries.Select(e => $"- {e}"))
            : "(empty)";

        var workerResultJson =
            $"{{\"status\":\"Ok\",\"summary\":\"Plan for {ticket.Id}\",\"filesChanged\":[],\"failureReason\":null," +
            $"\"metadata\":{{\"plan_html\":\"<your HTML plan here>\",\"risk_label\":\"low|medium|high\"," +
            $"\"size_label\":\"S|M|L\",\"planned_at_sha\":\"{repo.MainSha}\"}}}}";

        var vars = new Dictionary<string, string>
        {
            ["ticket_id"] = ticket.Id,
            ["title"] = ticket.Title,
            ["type"] = ticket.Type,
            ["size"] = ticket.Size.ToString(),
            ["risk"] = ticket.Risk.ToString(),
            ["description"] = StripHtml(ticket.DescriptionHtml),
            ["relations"] = relations,
            ["top_level_entries"] = topLevelEntries,
            ["worker_result_json"] = workerResultJson,
            ["main_sha"] = repo.MainSha
        };

        var instruction = TemplateLoader.Load("plan.md").Substitute(vars);

        return new Brief(
            ticket.Id,
            Phase.Plan,
            instruction,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>
            {
                ["main_sha"] = repo.MainSha,
                ["project_language"] = proj.Language,
                ["project_framework"] = proj.Framework,
                ["project_package_manager"] = proj.PackageManager,
                ["project_build_command"] = proj.BuildCommand,
                ["project_test_command"] = proj.TestCommand,
                ["project_install_command"] = proj.InstallCommand,
                ["project_dev_command"] = proj.DevCommand,
                ["project_plane_project_url"] = proj.PlaneProjectUrl,
                ["project_notes"] = proj.Notes
            });
    }

    private static string StripHtml(string html)
    {
        var stripped = Regex.Replace(html, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(stripped);
    }
}
