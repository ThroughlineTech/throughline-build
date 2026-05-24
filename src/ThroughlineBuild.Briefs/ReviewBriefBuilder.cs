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
        IReadOnlyList<CheckResult> checkResults)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("# Review Phase Brief");
        sb.AppendLine();
        sb.AppendLine("You are a reviewing agent. Your job is to assess the implementation against the ticket requirements and automated checks.");
        sb.AppendLine();

        // Ticket block
        sb.AppendLine("## Ticket");
        sb.AppendLine($"- Id: {ticket.Id}");
        sb.AppendLine($"- Title: {ticket.Title}");
        sb.AppendLine($"- Type: {ticket.Type}");
        sb.AppendLine($"- Size: {ticket.Size}");
        sb.AppendLine($"- Risk: {ticket.Risk}");
        sb.AppendLine();

        // Plan section (ticket description HTML verbatim)
        sb.AppendLine("## Plan (from ticket description)");
        sb.AppendLine(ticket.DescriptionHtml);
        sb.AppendLine();

        // Implementer summary
        sb.AppendLine("## Implementer summary");
        sb.AppendLine(implementerResult.Summary);
        sb.AppendLine();

        // Changed files section
        sb.AppendLine("## Changed files");
        if (diff.Entries.Count == 0)
        {
            sb.AppendLine("(no files changed)");
            sb.AppendLine();
            sb.AppendLine("Please confirm there is nothing to review.");
            sb.AppendLine();
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
                sb.AppendLine(fileLine);
            }
            sb.AppendLine();
        }

        // Patch content section
        sb.AppendLine("## Patch content");
        if (diff.Entries.Count > 0)
        {
            var entriesWithPatches = diff.Entries.Where(e => e.PatchContent != null).ToList();
            int remainingBudget = InstructionBudgetBytes - sb.Length;
            int patchesAdded = 0;

            if (entriesWithPatches.Count > 0)
            {
                int perFileBudget = Math.Max(remainingBudget / entriesWithPatches.Count, 2048);

                foreach (var entry in entriesWithPatches)
                {
                    if (sb.Length >= InstructionBudgetBytes)
                    {
                        sb.AppendLine($"- {entry.Path}: patch omitted (budget exhausted)");
                        continue;
                    }

                    var patchContent = entry.PatchContent!;
                    sb.AppendLine($"```diff");

                    if (patchContent.Length <= perFileBudget)
                    {
                        sb.AppendLine(patchContent);
                    }
                    else
                    {
                        var truncated = patchContent.Substring(0, perFileBudget);
                        sb.AppendLine(truncated);
                        int remainingChars = patchContent.Length - perFileBudget;
                        sb.AppendLine($"... [truncated: {remainingChars} more chars]");
                    }

                    sb.AppendLine("```");
                    sb.AppendLine();
                    patchesAdded++;
                }
            }
        }
        else
        {
            sb.AppendLine("(no patches)");
            sb.AppendLine();
        }

        // Automated checks section
        sb.AppendLine("## Automated checks");
        if (checkResults.Count == 0)
        {
            sb.AppendLine("(no automated checks configured)");
        }
        else
        {
            foreach (var check in checkResults)
            {
                var status = check.Passed ? "PASS" : "FAIL";
                sb.AppendLine($"- {check.Name}: {status} (exit {check.ExitCode}, elapsed {check.Elapsed.TotalSeconds:F1}s)");

                if (!check.Passed && !string.IsNullOrEmpty(check.StderrTail))
                {
                    var stdErr = check.StderrTail;
                    if (stdErr.Length > StderrTailBudgetPerFailure)
                    {
                        stdErr = stdErr.Substring(stdErr.Length - StderrTailBudgetPerFailure);
                    }
                    sb.AppendLine("```");
                    sb.AppendLine(stdErr);
                    sb.AppendLine("```");
                }
            }
        }
        sb.AppendLine();

        // WORKER_RESULT envelope and verdict guidance
        sb.AppendLine("## Required output");
        sb.AppendLine("Emit exactly one WORKER_RESULT block at the end of your response:");
        sb.AppendLine();
        sb.AppendLine("WORKER_RESULT");
        var workerResultJson =
            "{\"status\":\"Ok\",\"summary\":\"Review for " + ticket.Id + "\"," +
            "\"filesChanged\":[],\"failureReason\":null," +
            "\"metadata\":{\"verdict\":\"Pass|Rework|Fail\"," +
            "\"rationale\":\"<your rationale here>\"," +
            "\"checks_failed\":[\"check_name_if_applicable\"]}}";
        sb.AppendLine(workerResultJson);
        sb.AppendLine();

        sb.AppendLine("## Verdict guidance");
        sb.AppendLine("Choose verdict from:");
        sb.AppendLine("- Pass: implementation meets the plan, all checks pass");
        sb.AppendLine("- Rework: implementation has issues but is salvageable with changes");
        sb.AppendLine("- Fail: implementation does not meet requirements or is fundamentally broken");
        sb.AppendLine();
        sb.AppendLine("The checks_failed array should list names of specific automated checks that failed, if any.");

        // Build context dictionary
        var context = new Dictionary<string, string>
        {
            ["feature_branch"] = diff.ToRef,
            ["base_ref"] = diff.FromRef,
            ["files_changed_count"] = diff.Entries.Count.ToString(CultureInfo.InvariantCulture)
        };

        return new Brief(
            ticket.Id,
            Phase.Review,
            sb.ToString(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            context);
    }
}
