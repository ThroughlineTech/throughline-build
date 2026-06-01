using System.Text;
using System.Text.Json;

namespace ThroughlineBuild.Helpers;

// Renders PhaseSummary records to (a) human-readable text per the TLB-123 ticket spec
// and (b) JSON for downstream tooling consumption via --summary-json.
//
// The text layout is the user-visible CLI contract - keep stable; any change here
// breaks redirection-and-grep workflows (build plan TLB-N > out.txt).
public static class PhaseSummaryRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string RenderText(PhaseSummary summary)
    {
        return summary switch
        {
            PlanPhaseSummary p => RenderPlan(p),
            ImplementPhaseSummary i => RenderImplement(i),
            ReviewPhaseSummary r => RenderReview(r),
            ShipPhaseSummary s => RenderShip(s),
            DecomposePhaseSummary d => RenderDecompose(d),
            _ => RenderGeneric(summary),
        };
    }

    public static string RenderJson(PhaseSummary summary)
    {
        // Serialize as the typed record so order is deterministic per record shape.
        // No source-generated context required for net8.0 host (AOT is not the build target).
        return summary switch
        {
            PlanPhaseSummary p => JsonSerializer.Serialize(p, JsonOptions),
            ImplementPhaseSummary i => JsonSerializer.Serialize(i, JsonOptions),
            ReviewPhaseSummary r => JsonSerializer.Serialize(r, JsonOptions),
            ShipPhaseSummary s => JsonSerializer.Serialize(s, JsonOptions),
            DecomposePhaseSummary d => JsonSerializer.Serialize(d, JsonOptions),
            _ => JsonSerializer.Serialize(summary, JsonOptions),
        };
    }

    private static string RenderPlan(PlanPhaseSummary p)
    {
        var sb = new StringBuilder();
        if (!p.Success)
        {
            sb.AppendLine($"{p.TicketId} Plan Failed");
            sb.AppendLine();
            AppendFailureLines(sb, p);
            return sb.ToString().TrimEnd() + "\n";
        }

        sb.AppendLine($"{p.TicketId} Plan Complete");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(p.SizeLabel) || !string.IsNullOrEmpty(p.RiskLabel))
            sb.AppendLine($"Labels: size:{p.SizeLabel ?? "?"}, risk:{p.RiskLabel ?? "?"}");
        if (!string.IsNullOrEmpty(p.StateFrom) || !string.IsNullOrEmpty(p.StateTo))
            sb.AppendLine($"State: {p.StateFrom ?? "?"} -> {p.StateTo ?? "?"}");
        if (p.PlaneOps.Count > 0)
            sb.AppendLine($"Plane ops: {string.Join(", ", p.PlaneOps)}");
        if (!string.IsNullOrEmpty(p.InvestigatedAtSha))
            sb.AppendLine($"investigated_at_sha: {p.InvestigatedAtSha}");
        AppendWorkerLine(sb, p);
        if (!string.IsNullOrEmpty(p.PlaneUrl))
        {
            sb.AppendLine();
            sb.AppendLine($"Review the proposal in Plane: {p.PlaneUrl}");
        }
        sb.AppendLine($"Next: build implement {p.TicketId}");
        return sb.ToString();
    }

    private static string RenderImplement(ImplementPhaseSummary i)
    {
        var sb = new StringBuilder();
        if (!i.Success)
        {
            sb.AppendLine($"{i.TicketId} Implement Failed");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(i.BranchName))
                sb.AppendLine($"Branch: {i.BranchName}");
            AppendFailureLines(sb, i);
            return sb.ToString().TrimEnd() + "\n";
        }

        sb.AppendLine($"{i.TicketId} Implementation Complete");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(i.BranchName))
            sb.AppendLine($"Branch: {i.BranchName}");
        if (i.CommitCount > 0)
        {
            var shaList = string.Join(", ", i.CommitShas);
            sb.AppendLine($"Commits: {i.CommitCount} ({shaList})");
        }
        else if (!string.IsNullOrEmpty(i.CommitSha))
        {
            sb.AppendLine($"Commits: 1 ({i.CommitSha})");
        }
        sb.AppendLine($"Files Changed: {i.FilesChanged} ({i.FilesAdded} new + {i.FilesEdited} edit)");
        sb.AppendLine($"Tests Added: {i.TestsAddedCount}");
        if (!string.IsNullOrEmpty(i.StateFrom) || !string.IsNullOrEmpty(i.StateTo))
            sb.AppendLine($"State: {i.StateFrom ?? "?"} -> {i.StateTo ?? "?"}");
        AppendWorkerLine(sb, i);
        if (!string.IsNullOrEmpty(i.PlaneUrl))
        {
            sb.AppendLine();
            sb.AppendLine($"Plane: {i.PlaneUrl}");
        }
        sb.AppendLine($"Next: build review {i.TicketId}");
        return sb.ToString();
    }

    private static string RenderReview(ReviewPhaseSummary r)
    {
        var sb = new StringBuilder();
        if (!r.Success)
        {
            sb.AppendLine($"{r.TicketId} Review Failed");
            sb.AppendLine();
            AppendFailureLines(sb, r);
            return sb.ToString().TrimEnd() + "\n";
        }

        if (string.Equals(r.Verdict, "Pass", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"{r.TicketId} Review Complete");
            sb.AppendLine($"Next: build ship {r.TicketId}");
            return sb.ToString();
        }

        sb.AppendLine($"{r.TicketId} Review Complete");
        sb.AppendLine();
        sb.AppendLine($"Verifier: {(r.Verdict ?? "?")}");
        if (!string.IsNullOrEmpty(r.VerdictRationale))
            sb.AppendLine($"  rationale: {r.VerdictRationale}");
        sb.AppendLine($"Scope Violations: {(r.ChecksFailed.Count == 0 ? "none" : string.Join(", ", r.ChecksFailed))}");
        if (!string.IsNullOrEmpty(r.StateFrom) || !string.IsNullOrEmpty(r.StateTo))
            sb.AppendLine($"State: {r.StateFrom ?? "?"} -> {r.StateTo ?? "?"}");
        AppendWorkerLine(sb, r);
        if (!string.IsNullOrEmpty(r.PlaneUrl))
        {
            sb.AppendLine();
            sb.AppendLine($"Plane: {r.PlaneUrl}");
        }
        return sb.ToString();
    }

    private static string RenderShip(ShipPhaseSummary s)
    {
        var sb = new StringBuilder();
        if (!s.Success)
        {
            sb.AppendLine($"{s.TicketId} Ship Blocked");
            sb.AppendLine();
            AppendFailureLines(sb, s);
            return sb.ToString().TrimEnd() + "\n";
        }

        sb.AppendLine($"{s.TicketId} Shipped");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(s.BranchName) && !string.IsNullOrEmpty(s.MergedSha))
            sb.AppendLine($"Merged: {s.BranchName} -> main (fast-forward, {s.MergedSha})");
        else if (!string.IsNullOrEmpty(s.MergedSha))
            sb.AppendLine($"Merged: -> main (fast-forward, {s.MergedSha})");
        sb.AppendLine($"Files: {s.FilesChanged} changed");
        if (!string.IsNullOrEmpty(s.StateFrom) || !string.IsNullOrEmpty(s.StateTo))
            sb.AppendLine($"State: {s.StateFrom ?? "?"} -> {s.StateTo ?? "?"}");
        sb.AppendLine($"Archived: {(s.Archived ? "yes (worktree removed)" : "no")}");
        if (!string.IsNullOrEmpty(s.PlaneUrl))
        {
            sb.AppendLine();
            sb.AppendLine($"Plane: {s.PlaneUrl}");
        }
        return sb.ToString();
    }

    private static string RenderDecompose(DecomposePhaseSummary d)
    {
        var sb = new StringBuilder();
        if (!d.Success)
        {
            sb.AppendLine($"{d.TicketId} Decompose Failed");
            sb.AppendLine();
            AppendFailureLines(sb, d);
            return sb.ToString().TrimEnd() + "\n";
        }

        sb.AppendLine($"{d.TicketId} Decomposed");
        sb.AppendLine();
        sb.AppendLine($"Children: {d.ChildCount}");
        for (int i = 0; i < d.CreatedIds.Count; i++)
        {
            var id = d.CreatedIds[i];
            var size = i < d.ChildSizes.Count ? d.ChildSizes[i] : "?";
            sb.AppendLine($"  {id} (size:{size})");
        }
        if (!string.IsNullOrEmpty(d.StateFrom) || !string.IsNullOrEmpty(d.StateTo))
            sb.AppendLine($"State: {d.StateFrom ?? "?"} -> {d.StateTo ?? "?"}");
        AppendWorkerLine(sb, d);
        if (!string.IsNullOrEmpty(d.PlaneUrl))
        {
            sb.AppendLine();
            sb.AppendLine($"Plane: {d.PlaneUrl}");
        }
        if (d.CreatedIds.Count > 0)
            sb.AppendLine($"Next: build chain {d.CreatedIds[0]}");
        return sb.ToString();
    }

    private static string RenderGeneric(PhaseSummary g)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{g.TicketId} {g.PhaseKind} {(g.Success ? "Complete" : "Failed")}");
        if (!g.Success)
        {
            sb.AppendLine();
            AppendFailureLines(sb, g);
        }
        return sb.ToString();
    }

    private static void AppendWorkerLine(StringBuilder sb, PhaseSummary s)
    {
        if (s.WorkerWallMs is null && s.OutputTokens is null && s.CacheReadTokens is null)
            return;
        var parts = new List<string>();
        if (s.WorkerWallMs is long wall) parts.Add($"{wall} ms");
        if (s.OutputTokens is long ot) parts.Add($"{ot} out");
        if (s.CacheReadTokens is long cr) parts.Add($"{cr} cache-read");
        sb.AppendLine("Worker: " + string.Join(" / ", parts));
    }

    private static void AppendFailureLines(StringBuilder sb, PhaseSummary s)
    {
        if (!string.IsNullOrEmpty(s.FailedAt))
            sb.AppendLine($"Failed at: {s.FailedAt}");
        if (!string.IsNullOrEmpty(s.FailureReason))
            sb.AppendLine($"Reason: {s.FailureReason}");
        if (!string.IsNullOrEmpty(s.SessionArtifactsPath))
            sb.AppendLine($"Artifacts: {s.SessionArtifactsPath}");
        if (!string.IsNullOrEmpty(s.PlaneUrl))
            sb.AppendLine($"Plane: {s.PlaneUrl}");
    }
}
