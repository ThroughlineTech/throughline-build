using System.Globalization;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs;

public static class BatchReviewBriefBuilder
{
    // Inline the combined diff when it fits; otherwise tell the reviewer to read it itself
    // from the worktree. We never truncate - a truncated patch reads as missing code and
    // drives false-negative rework, and the combined stack diff is even larger than a
    // single-ticket diff. See TLB-477 and the matching note in ReviewBriefBuilder.
    private const int InlinePatchBudgetBytes = 300 * 1024;
    private const int StderrTailBudgetPerFailure = 2048;
    private const int StdoutTailBudgetPerFailure = 2048;

    /// <summary>
    /// Builds a combined review brief covering the full batch stack diff.
    /// Each ticket is listed with its commit range so the reviewer can scope
    /// the diff per ticket while assessing the integrated design.
    /// </summary>
    public static Brief Build(
        string agentName,
        IReadOnlyList<Ticket> tickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        string baseRef,
        GitDiff diff,
        IReadOnlyList<CheckResult> checkResults,
        ProjectContext? project = null)
    {
        if (tickets.Count == 0)
            throw new ArgumentException("Batch review brief requires at least one ticket.", nameof(tickets));

        var proj = project ?? ProjectContext.Empty;

        var ticketSections = BuildTicketSections(tickets, confirmedTickets, baseRef);
        var changedFilesSection = BuildChangedFilesSection(diff);
        var patchContentSection = BuildPatchContentSection(diff);
        var automatedChecksSection = BuildAutomatedChecksSection(checkResults);
        var workerResultJson = BuildWorkerResultJson();

        var vars = new Dictionary<string, string>
        {
            ["ticket_count"] = tickets.Count.ToString(CultureInfo.InvariantCulture),
            ["ticket_sections"] = ticketSections,
            ["changed_files_section"] = changedFilesSection,
            ["patch_content_section"] = patchContentSection,
            ["automated_checks_section"] = automatedChecksSection,
            ["worker_result_json"] = workerResultJson
        };

        var template = TemplateLoader.Load(agentName, "batch-review.md");
        var instruction = template.Substitute(vars);

        var context = new Dictionary<string, string>
        {
            ["feature_branch"] = diff.ToRef,
            ["base_ref"] = diff.FromRef,
            ["files_changed_count"] = diff.Entries.Count.ToString(CultureInfo.InvariantCulture),
            ["ticket_count"] = tickets.Count.ToString(CultureInfo.InvariantCulture),
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
            string.Join(", ", tickets.Select(t => t.Id)),
            Phase.Review,
            instruction,
            Array.Empty<string>(),
            Array.Empty<string>(),
            context);
    }

    /// <summary>
    /// Builds one section per ticket, each prefixed with its commit range.
    /// Range for ticket at stack position N:
    ///   from = baseRef (for position 1) or the previous ticket's commit SHA
    ///   to   = this ticket's commit SHA from confirmedTickets
    /// </summary>
    private static string BuildTicketSections(
        IReadOnlyList<Ticket> tickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        string baseRef)
    {
        var sb = new StringBuilder();
        var sorted = confirmedTickets.OrderBy(t => t.StackPosition).ToList();

        for (int i = 0; i < tickets.Count; i++)
        {
            if (i > 0) sb.Append('\n');

            var ticket = tickets[i];
            var confirmed = sorted.FirstOrDefault(
                c => string.Equals(c.TicketId, ticket.Id, StringComparison.Ordinal));

            string fromSha = i == 0
                ? AbbrevSha(baseRef)
                : (sorted.Count >= i ? AbbrevSha(sorted[i - 1].CommitSha) : AbbrevSha(baseRef));
            string toSha = confirmed is not null ? AbbrevSha(confirmed.CommitSha) : "(unknown)";
            string commitRange = $"{fromSha}..{toSha}";

            sb.Append($"### {i + 1}. {ticket.Id} - {ticket.Title}\n");
            sb.Append($"- Commit range: `{commitRange}`\n");
            sb.Append($"- Stack position: {i + 1}\n");
            sb.Append('\n');
            sb.Append("Plan (raw HTML from ticket description; read directly, do not re-render):\n");
            sb.Append(ticket.DescriptionHtml);
            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string AbbrevSha(string sha) =>
        sha.Length >= 7 ? sha.Substring(0, 7) : sha;

    private static string BuildChangedFilesSection(GitDiff diff)
    {
        var sb = new StringBuilder();
        sb.Append("## Combined changed files\n");
        if (diff.Entries.Count == 0)
        {
            sb.Append("(no files changed)\n");
            sb.Append('\n');
            sb.Append("Please confirm there is nothing to review.\n");
            sb.Append('\n');
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
                    fileLine += $" (was: {entry.OldPath})";
                sb.Append(fileLine);
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Inline the full combined diff when it fits InlinePatchBudgetBytes; otherwise hand the
    // reviewer the read-only command to pull it from the worktree. Never truncate - see the
    // class-level note and TLB-477.
    private static string BuildPatchContentSection(GitDiff diff)
    {
        var sb = new StringBuilder();
        sb.Append("## Combined patch content\n");

        if (diff.Entries.Count == 0)
        {
            sb.Append("(no patches)\n");
            sb.Append('\n');
            return sb.ToString();
        }

        var entriesWithPatches = diff.Entries.Where(e => e.PatchContent != null).ToList();
        long totalPatchBytes = entriesWithPatches.Sum(e => (long)e.PatchContent!.Length);

        if (entriesWithPatches.Count == 0)
        {
            AppendFetchDirective(
                sb,
                diff,
                "Patch content was not captured for this review, so the combined diff is not inlined below.");
            return sb.ToString();
        }

        if (totalPatchBytes > InlinePatchBudgetBytes)
        {
            var sizeKb = (totalPatchBytes / 1024).ToString(CultureInfo.InvariantCulture);
            AppendFetchDirective(
                sb,
                diff,
                $"The combined diff is large ({sizeKb} KB of patch across {entriesWithPatches.Count} file(s)) and is not inlined below.");
            return sb.ToString();
        }

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

    // Emits the "read the diff yourself" block: the reason, the exact read-only command
    // to view each file's diff in the worktree, and a read-only reminder. The prose lives in
    // a shared template; the reason line and diff refs are the dynamic bits. The template ends
    // with a single newline; the trailing blank line is appended here in C#.
    private static void AppendFetchDirective(StringBuilder sb, GitDiff diff, string reason)
    {
        sb.Append(TemplateLoader.LoadShared("patch-fetch-directive-batch.md").Substitute(
            new Dictionary<string, string>
            {
                ["reason"] = reason,
                ["from_ref"] = diff.FromRef,
                ["to_ref"] = diff.ToRef
            }));
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
                    AppendStream(sb, "stdout", check.StdoutTail, StdoutTailBudgetPerFailure);
                    AppendStream(sb, "stderr", check.StderrTail, StderrTailBudgetPerFailure);
                }
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static void AppendStream(StringBuilder sb, string label, string? stream, int budget)
    {
        if (string.IsNullOrEmpty(stream)) return;
        var tail = stream.Length > budget ? stream.Substring(stream.Length - budget) : stream;
        sb.Append(label).Append(":\n```\n").Append(tail).Append('\n').Append("```\n");
    }

    private static string BuildWorkerResultJson() =>
        TemplateLoader.LoadShared("batch-review-worker-result.md");
}
