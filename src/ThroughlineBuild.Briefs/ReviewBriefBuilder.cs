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
        ProjectContext? project = null,
        IReadOnlyList<SmokeSignal>? smokeSignals = null)
    {
        var proj = project ?? ProjectContext.Empty;

        var changedFilesSection = BuildChangedFilesSection(diff);
        var patchContentSection = BuildPatchContentSection(diff);
        var automatedChecksSection = BuildAutomatedChecksSection(checkResults);

        var workerResultJson = TemplateLoader.LoadShared("review-worker-result.md")
            .Substitute(new Dictionary<string, string> { ["ticket_id"] = ticket.Id });

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

        // Append smoke signals collected by the gate as advisory context for the reviewer.
        // Signals are non-gating (never caused a hard-fail) so they are informational priors.
        if (smokeSignals is { Count: > 0 })
            instruction = instruction + "\n" + BuildSmokeSignalsSection(smokeSignals);

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
    // The prose lives in a shared template; the reason line and diff refs are the dynamic bits.
    // The template ends with a single newline; the trailing blank line that separates this
    // block from the next section is appended here in C#.
    private static void AppendFetchDirective(StringBuilder sb, GitDiff diff, string reason)
    {
        sb.Append(TemplateLoader.LoadShared("patch-fetch-directive-single.md").Substitute(
            new Dictionary<string, string>
            {
                ["reason"] = reason,
                ["from_ref"] = diff.FromRef,
                ["to_ref"] = diff.ToRef
            }));
        sb.Append('\n');
    }

    // Check role semantics are a cross-phase contract: advisory failures must never hard-fail
    // the gate, drive a Rework verdict, or block ship. The gate and ship enforce this on their
    // side; here we enforce it at the verifier's INPUT by splitting advisory results into a
    // separate, explicitly-framed informational section instead of presenting them
    // undifferentiated. Presenting a failing advisory check as just another failed check made
    // the verifier return Rework for a cosmetic lint finding twice and kill the chain at the
    // rework cap with every acceptance criterion passing.
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
            var nonAdvisory = checkResults.Where(c => c.Role != CheckRole.Advisory).ToList();
            var advisory = checkResults.Where(c => c.Role == CheckRole.Advisory).ToList();

            if (nonAdvisory.Count == 0)
                sb.Append("(no gating checks configured)\n");
            foreach (var check in nonAdvisory)
                AppendCheckResult(sb, check);

            if (advisory.Count > 0)
            {
                sb.Append("\n### Advisory checks (informational)\n");
                sb.Append("The checks below are configured as advisory (cosmetic, auto-fixable findings). ");
                sb.Append("They never block: do NOT list them in checks_failed, and a Rework or Fail verdict ");
                sb.Append("must not rest solely on advisory findings. You may mention them in your rationale as notes.\n");
                foreach (var check in advisory)
                    AppendCheckResult(sb, check);
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static void AppendCheckResult(StringBuilder sb, CheckResult check)
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

    private static string BuildSmokeSignalsSection(IReadOnlyList<SmokeSignal> signals)
    {
        var sb = new StringBuilder();
        sb.Append("\n## Smoke signals (gate advisory)\n");
        sb.Append("These observations were collected by the gate phase and are non-blocking. Use them as context.\n\n");
        foreach (var s in signals)
        {
            var matched = s.Matched ? "ok" : "flag";
            sb.Append($"- [{matched}] {s.Label}: {s.Details}\n");
        }
        sb.Append('\n');
        return sb.ToString();
    }
}
