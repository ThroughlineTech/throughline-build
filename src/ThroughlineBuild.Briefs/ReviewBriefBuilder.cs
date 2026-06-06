using System.Globalization;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs;

public static class ReviewBriefBuilder
{
    // The reviewer is a full agent dispatched into the feature worktree (the branch is
    // checked out), so it can fetch anything it needs with read-only git. We inline the
    // complete diff when it is small enough to be cheap, and fall back to "go read it
    // yourself" for large diffs rather than truncating. Truncating the diff makes the
    // reviewer reject code it never saw; because each rework round grows the diff, more
    // gets truncated each pass, which turns into an unbreakable rework loop (the TLB-475
    // chain hit the rework cap this way with every automated check passing). See TLB-477.
    const int InlinePatchBudgetBytes = 300 * 1024;
    const int StderrTailBudgetPerFailure = 2048;
    const int StdoutTailBudgetPerFailure = 2048;

    public static Brief Build(
        string agentName,
        Ticket ticket,
        GitDiff diff,
        WorkerResult implementerResult,
        IReadOnlyList<CheckResult> checkResults,
        ProjectContext? project = null)
    {
        var proj = project ?? ProjectContext.Empty;

        var changedFilesSection = BuildChangedFilesSection(diff);
        var patchContentSection = BuildPatchContentSection(diff);
        var automatedChecksSection = BuildAutomatedChecksSection(checkResults);

        var workerResultJson =
            "{\"status\":\"Ok\",\"summary\":\"Review for " + ticket.Id + "\"," +
            "\"files_changed\":[],\"failure_reason\":null," +
            "\"metadata\":{\"verdict\":\"Pass|Rework|Fail\"," +
            "\"rationale_ref\":\"REVIEW_CRITIQUE\"," +
            "\"checks_failed\":[\"check_name_if_applicable\"]}}";

        var template = TemplateLoader.Load(agentName, "review.md");

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
            ["patch_content_section"] = patchContentSection,
            ["automated_checks_section"] = automatedChecksSection,
            ["worker_result_json"] = workerResultJson
        };

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

    // Inline the full diff when it fits InlinePatchBudgetBytes; otherwise hand the reviewer
    // the read-only command to pull each file's diff from the worktree itself. We never
    // truncate a patch: a half-shown patch reads as "missing code" to the verifier and
    // drives false-negative rework (see TLB-477 / the class-level comment).
    private static string BuildPatchContentSection(GitDiff diff)
    {
        var sb = new StringBuilder();
        sb.Append("## Patch content\n");

        if (diff.Entries.Count == 0)
        {
            sb.Append("(no patches)\n");
            sb.Append('\n');
            return sb.ToString();
        }

        var entriesWithPatches = diff.Entries.Where(e => e.PatchContent != null).ToList();
        long totalPatchBytes = entriesWithPatches.Sum(e => (long)e.PatchContent!.Length);

        // Fetch-on-demand: either no inline patch was captured, or the diff is too large
        // to inline cheaply. Either way the branch is checked out in the reviewer's working
        // directory, so it reads the change itself with read-only git.
        if (entriesWithPatches.Count == 0)
        {
            AppendFetchDirective(
                sb,
                diff,
                "Patch content was not captured for this review, so the diff is not inlined below.");
            return sb.ToString();
        }

        if (totalPatchBytes > InlinePatchBudgetBytes)
        {
            var sizeKb = (totalPatchBytes / 1024).ToString(CultureInfo.InvariantCulture);
            AppendFetchDirective(
                sb,
                diff,
                $"The diff is large ({sizeKb} KB of patch across {entriesWithPatches.Count} file(s)) and is not inlined below.");
            return sb.ToString();
        }

        // Inline mode: the whole diff fits, so include every file's patch in full.
        foreach (var entry in entriesWithPatches)
        {
            sb.Append("```diff\n");
            sb.Append(entry.PatchContent);
            sb.Append('\n');
            sb.Append("```\n");
            sb.Append('\n');
        }

        return sb.ToString();
    }

    // Emits the "read the diff yourself" block: a one-line reason, the exact read-only
    // command to view each file's diff from the worktree, and a reminder to stay read-only.
    private static void AppendFetchDirective(StringBuilder sb, GitDiff diff, string reason)
    {
        sb.Append(reason);
        sb.Append('\n');
        sb.Append('\n');
        sb.Append("The feature branch is checked out in your working directory. You MUST read the\n");
        sb.Append("changes yourself before judging - for each file in the list above, view its diff:\n");
        sb.Append('\n');
        sb.Append("```\n");
        sb.Append($"git diff {diff.FromRef}...{diff.ToRef} -- <path>\n");
        sb.Append("```\n");
        sb.Append('\n');
        sb.Append("You may also `git show`, `git log`, or read the file directly for surrounding\n");
        sb.Append("context. Do not run any git command that writes (no stash/checkout/reset/rebase).\n");
        sb.Append('\n');
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

                if (!check.Passed)
                {
                    // Many toolchains write fatal errors to stdout, not stderr (e.g. dotnet's
                    // MSB1003, tsc, vite). Surfacing only stderr left such failures invisible to the
                    // verifier, turning a one-line misconfig into a silent rework loop. Emit both.
                    AppendStream(sb, "stdout", check.StdoutTail, StdoutTailBudgetPerFailure);
                    AppendStream(sb, "stderr", check.StderrTail, StderrTailBudgetPerFailure);
                }
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    // Emit the tail of a captured stream as a labeled fenced block, keeping only the last
    // `budget` chars (the end holds the error). No-op for empty streams.
    private static void AppendStream(StringBuilder sb, string label, string? stream, int budget)
    {
        if (string.IsNullOrEmpty(stream)) return;
        var tail = stream.Length > budget ? stream.Substring(stream.Length - budget) : stream;
        sb.Append(label).Append(":\n");
        sb.Append("```\n");
        sb.Append(tail);
        sb.Append('\n');
        sb.Append("```\n");
    }
}
