using System.Globalization;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs;

public static class ReviewBriefBuilder
{
    const int InstructionBudgetBytes = 50 * 1024;
    const int StderrTailBudgetPerFailure = 2048;

    public static Brief Build(
        Ticket ticket,
        GitDiff diff,
        WorkerResult implementerResult,
        IReadOnlyList<CheckResult> checkResults,
        ProjectContext? project = null)
    {
        var proj = project ?? ProjectContext.Empty;

        var changedFilesSection = BuildChangedFilesSection(diff);
        var automatedChecksSection = BuildAutomatedChecksSection(checkResults);

        var workerResultJson =
            "{\"status\":\"Ok\",\"summary\":\"Review for " + ticket.Id + "\"," +
            "\"filesChanged\":[],\"failureReason\":null," +
            "\"metadata\":{\"verdict\":\"Pass|Rework|Fail\"," +
            "\"rationale\":\"<your rationale here>\"," +
            "\"checks_failed\":[\"check_name_if_applicable\"]}}";

        var template = TemplateLoader.Load("review.md");

        var vars = new Dictionary<string, string>
        {
            ["ticket_id"] = ticket.Id,
            ["title"] = ticket.Title,
            ["type"] = ticket.Type,
            ["size"] = ticket.Size.ToString(),
            ["risk"] = ticket.Risk.ToString(),
            ["description_html"] = ticket.DescriptionHtml,
            ["implementer_summary"] = implementerResult.Summary,
            ["changed_files_section"] = changedFilesSection,
            ["patch_content_section"] = "",
            ["automated_checks_section"] = automatedChecksSection,
            ["worker_result_json"] = workerResultJson
        };

        // Budget for the patch body is derived from the substituted template with patch_content_section
        // initially empty. This mirrors the original sb.Length-based budget semantically: total instruction
        // length is bounded by InstructionBudgetBytes.
        var lengthWithoutPatch = template.Substitute(vars).Length;
        int remainingBudget = InstructionBudgetBytes - lengthWithoutPatch;

        vars["patch_content_section"] = BuildPatchContentSection(diff, remainingBudget);

        var instruction = template.Substitute(vars);

        var context = new Dictionary<string, string>
        {
            ["feature_branch"] = diff.ToRef,
            ["base_ref"] = diff.FromRef,
            ["files_changed_count"] = diff.Entries.Count.ToString(CultureInfo.InvariantCulture),
            ["project_language"] = proj.Language,
            ["project_framework"] = proj.Framework,
            ["project_package_manager"] = proj.PackageManager,
            ["project_build_command"] = proj.BuildCommand,
            ["project_test_command"] = proj.TestCommand,
            ["project_install_command"] = proj.InstallCommand,
            ["project_dev_command"] = proj.DevCommand,
            ["project_plane_project_url"] = proj.PlaneProjectUrl,
            ["project_notes"] = proj.Notes
        };

        return new Brief(
            ticket.Id,
            Phase.Review,
            instruction,
            Array.Empty<string>(),
            Array.Empty<string>(),
            context);
    }

    private static string BuildChangedFilesSection(GitDiff diff)
    {
        var sb = new StringBuilder();
        sb.Append("## Changed files\n");
        if (diff.Entries.Count == 0)
        {
            sb.Append("(no files changed)\n");
            sb.Append("\n");
            sb.Append("Please confirm there is nothing to review.\n");
            sb.Append("\n");
        }
        else
        {
            foreach (var entry in diff.Entries)
            {
                var kindStr = entry.Kind switch
                {
                    DiffKind.Added => "Added",
                    DiffKind.Modified => "Modified",
                    DiffKind.Deleted => "Deleted",
                    DiffKind.Renamed => "Renamed",
                    _ => "Unknown"
                };

                var fileLine = $"- {kindStr} {entry.Path} (+{entry.LinesAdded}/-{entry.LinesRemoved})";
                if (entry.Kind == DiffKind.Renamed && entry.OldPath != null)
                {
                    fileLine += $" (was: {entry.OldPath})";
                }
                sb.Append(fileLine);
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildPatchContentSection(GitDiff diff, int remainingBudget)
    {
        var sb = new StringBuilder();
        sb.Append("## Patch content\n");
        if (diff.Entries.Count > 0)
        {
            var entriesWithPatches = diff.Entries.Where(e => e.PatchContent != null).ToList();

            if (entriesWithPatches.Count > 0)
            {
                int perFileBudget = Math.Max(remainingBudget / entriesWithPatches.Count, 2048);

                foreach (var entry in entriesWithPatches)
                {
                    if (sb.Length >= remainingBudget)
                    {
                        sb.Append($"- {entry.Path}: patch omitted (budget exhausted)\n");
                        continue;
                    }

                    var patchContent = entry.PatchContent!;
                    sb.Append("```diff\n");

                    if (patchContent.Length <= perFileBudget)
                    {
                        sb.Append(patchContent);
                        sb.Append('\n');
                    }
                    else
                    {
                        var truncated = patchContent.Substring(0, perFileBudget);
                        sb.Append(truncated);
                        sb.Append('\n');
                        int remainingChars = patchContent.Length - perFileBudget;
                        sb.Append($"... [truncated: {remainingChars} more chars]\n");
                    }

                    sb.Append("```\n");
                    sb.Append('\n');
                }
            }
        }
        else
        {
            sb.Append("(no patches)\n");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildAutomatedChecksSection(IReadOnlyList<CheckResult> checkResults)
    {
        var sb = new StringBuilder();
        sb.Append("## Automated checks\n");
        if (checkResults.Count == 0)
        {
            sb.Append("(no automated checks configured)\n");
        }
        else
        {
            foreach (var check in checkResults)
            {
                var status = check.Passed ? "PASS" : "FAIL";
                sb.Append($"- {check.Name}: {status} (exit {check.ExitCode}, elapsed {check.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s)\n");

                if (!check.Passed && !string.IsNullOrEmpty(check.StderrTail))
                {
                    var stdErr = check.StderrTail;
                    if (stdErr.Length > StderrTailBudgetPerFailure)
                    {
                        stdErr = stdErr.Substring(stdErr.Length - StderrTailBudgetPerFailure);
                    }
                    sb.Append("```\n");
                    sb.Append(stdErr);
                    sb.Append('\n');
                    sb.Append("```\n");
                }
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }
}
