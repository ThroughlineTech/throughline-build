using System.Text.RegularExpressions;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs;

public record RepoState(string MainSha, IReadOnlyList<string> TopLevelEntries);

public static class PlanBriefBuilder
{
    public static Brief Build(Ticket ticket, RepoState repo)
    {
        var strippedDescription = StripHtml(ticket.DescriptionHtml);

        var relations = ticket.Relations.Count > 0
            ? string.Join("\n", ticket.Relations.Select(r => $"- {r.Kind}: {r.TargetId}"))
            : "(none)";

        var entries = repo.TopLevelEntries.Count > 0
            ? string.Join("\n", repo.TopLevelEntries.Select(e => $"- {e}"))
            : "(empty)";

        var workerResultJson =
            $"{{\"status\":\"Ok\",\"summary\":\"Plan for {ticket.Id}\",\"filesChanged\":[],\"failureReason\":null," +
            $"\"metadata\":{{\"plan_html\":\"<your HTML plan here>\",\"risk_label\":\"low|medium|high\"," +
            $"\"size_label\":\"S|M|L\",\"planned_at_sha\":\"{repo.MainSha}\"}}}}";

        var instruction =
            $"# Plan Phase Brief\n\n" +
            $"You are a planning agent. Your ONLY job is to produce a plan_html and labels for ticket {ticket.Id}.\n" +
            $"Do NOT modify any files. Do NOT make any tool calls that write to disk.\n\n" +
            $"## Ticket\n" +
            $"- Id: {ticket.Id}\n" +
            $"- Title: {ticket.Title}\n" +
            $"- Type: {ticket.Type}\n" +
            $"- Size: {ticket.Size}\n" +
            $"- Risk: {ticket.Risk}\n\n" +
            $"## Description\n{strippedDescription}\n\n" +
            $"## Relations\n{relations}\n\n" +
            $"## Repo top-level entries\n{entries}\n\n" +
            $"## Required output\n" +
            $"Emit exactly one WORKER_RESULT block at the end of your response:\n\n" +
            $"WORKER_RESULT\n{workerResultJson}\n\n" +
            $"## Constraints\n" +
            $"- Planning only - no file writes\n" +
            $"- plan_html must be valid HTML\n" +
            $"- risk_label must be exactly: low, medium, or high\n" +
            $"- size_label must be exactly: S, M, or L\n" +
            $"- planned_at_sha must be: {repo.MainSha}";

        return new Brief(
            ticket.Id,
            Phase.Plan,
            instruction,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string> { ["main_sha"] = repo.MainSha });
    }

    private static string StripHtml(string html)
    {
        var stripped = Regex.Replace(html, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(stripped);
    }
}
