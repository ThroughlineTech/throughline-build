using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs;

public record RepoState(string MainSha, IReadOnlyList<string> TopLevelEntries);

public static class PlanBriefBuilder
{
    public static Brief Build(string agentName, Ticket ticket, RepoState repo, ProjectContext? project = null)
    {
        var proj = project ?? ProjectContext.Empty;

        var topLevelEntries = repo.TopLevelEntries.Count > 0
            ? string.Join(", ", repo.TopLevelEntries)
            : "(empty)";

        var projectNotesSection = string.IsNullOrWhiteSpace(proj.Notes)
            ? string.Empty
            : $"## Project notes\n\n{proj.Notes}\n";

        var vars = new Dictionary<string, string>
        {
            ["ticket_id"] = ticket.Id,
            ["title"] = ticket.Title,
            ["type"] = ticket.Type,
            ["description_html"] = ticket.DescriptionHtml,
            ["top_level_entries"] = topLevelEntries,
            ["main_sha"] = repo.MainSha,
            ["project_notes_section"] = projectNotesSection,
            ["workflow_tool"] = proj.WorkflowTool,
            // Agent-agnostic obsolete-detection prose + escalate envelope example, shared across
            // all four plan templates (mirrors ImplementBriefBuilder). Plan is one-shot (no rework
            // round), so there is no suppressed variant - always the initial template.
            ["obsolete_detection_section"] = TemplateLoader.LoadShared("plan-obsolete-initial.md")
        };

        var instruction = TemplateLoader.Load(agentName, "plan.md").Substitute(vars);

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
                ["project_notes"] = proj.Notes,
                ["project_workflow_tool"] = proj.WorkflowTool
            });
    }
}
