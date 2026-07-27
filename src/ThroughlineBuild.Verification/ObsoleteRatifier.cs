using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Verification;

/// <summary>
/// Verifies an obsolete escalation claim by performing three checks:
/// (1) cited commit exists in the repo, (2) cited files exist at HEAD,
/// (3) a model-driven check that the prior work satisfies this brief's acceptance criteria.
/// Returns a Verdict (Pass = ratified, Fail/Rework = rejected).
/// </summary>
public sealed class ObsoleteRatifier : IObsoleteRatifier
{
    private readonly IWorkerAgent _worker;
    private readonly WorkerOptions _workerOptions;
    private readonly string _workingDirectory;
    private readonly IGitClient? _git;

    public ObsoleteRatifier(
        IWorkerAgent worker,
        WorkerOptions workerOptions,
        string workingDirectory,
        IGitClient? git = null)
    {
        _worker = worker;
        _workerOptions = workerOptions;
        _workingDirectory = workingDirectory;
        _git = git;
    }

    public async Task<Verdict> RatifyAsync(Ticket ticket, WorkerResult escalateResult, string? evidenceDirectory, CancellationToken ct)
    {
        // Resolve evidence against the worktree where the escalation was raised. A chain
        // rework round runs in the ticket's shared worktree, so its new files are not present
        // in the main worktree the ratifier was constructed against; checking there would fail
        // Check 2 spuriously (cited file "not found at HEAD"). Fall back to the construction-time
        // directory when no override is supplied.
        var dir = string.IsNullOrEmpty(evidenceDirectory) ? _workingDirectory : evidenceDirectory;

        var evidence = ExtractSubsumedByEvidence(escalateResult);
        if (evidence is null)
            return new Verdict(VerdictKind.Fail,
                "obsolete escalation missing valid subsumed_by evidence",
                Array.Empty<string>());

        // Check 1: cited commit exists in the repo
        if (_git is not null)
        {
            try
            {
                await _git.RevParseAsync($"{evidence.Commit}^{{commit}}", dir, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return new Verdict(VerdictKind.Fail,
                    $"ratification failed: cited commit {evidence.Commit} not found in repo",
                    Array.Empty<string>());
            }
        }

        // Check 2: cited files exist at HEAD
        var missing = evidence.Files
            .Where(f => !File.Exists(Path.Combine(dir, f)))
            .ToList();
        if (missing.Count > 0)
            return new Verdict(VerdictKind.Fail,
                $"ratification failed: cited files not found at HEAD: {string.Join(", ", missing)}",
                Array.Empty<string>());

        // Check 3: model-driven acceptance criteria verification
        var brief = BuildRatificationBrief(ticket, evidence);
        var workerResult = await _worker.ExecuteAsync(brief, dir, _workerOptions, ct)
            .ConfigureAwait(false);

        if (workerResult.Status != Status.Ok)
        {
            var reason = workerResult.FailureReason ?? workerResult.Status.ToString();
            return new Verdict(VerdictKind.Fail,
                $"ratification worker failed: {reason}",
                Array.Empty<string>());
        }

        return ParseVerdict(workerResult.Metadata);
    }

    private static SubsumedByEvidence? ExtractSubsumedByEvidence(WorkerResult escalateResult)
    {
        if (!escalateResult.Metadata.TryGetValue("escalation", out var escalationObj))
            return null;
        if (escalationObj is not JsonElement escalationElem ||
            escalationElem.ValueKind != JsonValueKind.Object)
            return null;
        if (!escalationElem.TryGetProperty("subsumed_by", out var subsumedByElem) ||
            subsumedByElem.ValueKind != JsonValueKind.Object)
            return null;

        var commit = subsumedByElem.TryGetProperty("commit", out var commitElem) &&
                     commitElem.ValueKind == JsonValueKind.String
            ? commitElem.GetString() : null;
        var rationale = subsumedByElem.TryGetProperty("rationale", out var rationaleElem) &&
                        rationaleElem.ValueKind == JsonValueKind.String
            ? rationaleElem.GetString() : null;

        if (string.IsNullOrEmpty(commit) || string.IsNullOrEmpty(rationale))
            return null;

        var files = new List<string>();
        if (subsumedByElem.TryGetProperty("files", out var filesElem) &&
            filesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileElem in filesElem.EnumerateArray())
            {
                if (fileElem.ValueKind == JsonValueKind.String)
                {
                    var f = fileElem.GetString();
                    if (!string.IsNullOrEmpty(f)) files.Add(f);
                }
            }
        }

        return new SubsumedByEvidence(commit, files.AsReadOnly(), rationale);
    }

    private static Brief BuildRatificationBrief(Ticket ticket, SubsumedByEvidence evidence)
    {
        var vars = new Dictionary<string, string>
        {
            ["ticket_id"] = ticket.Id,
            ["ticket_title"] = ticket.Title,
            ["ticket_description_html"] = ticket.DescriptionHtml,
            ["evidence_commit"] = evidence.Commit,
            ["evidence_files"] = string.Join(", ", evidence.Files),
            ["evidence_rationale"] = evidence.Rationale,
        };

        var instruction = RatificationPromptLoader.Load().Substitute(vars);

        return new Brief(
            ticket.Id,
            Phase.Review,
            instruction,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>());
    }

    private static Verdict ParseVerdict(IReadOnlyDictionary<string, object> metadata)
    {
        var raw = TryGetString(metadata, "verdict");
        string rationale = TryGetString(metadata, "rationale") ?? "";

        if (string.Equals(raw, "Pass", StringComparison.OrdinalIgnoreCase))
            return new Verdict(VerdictKind.Pass, rationale, Array.Empty<string>());

        if (string.Equals(raw, "Rework", StringComparison.OrdinalIgnoreCase))
            return new Verdict(VerdictKind.Rework, rationale, Array.Empty<string>());

        if (string.Equals(raw, "Fail", StringComparison.OrdinalIgnoreCase))
            return new Verdict(VerdictKind.Fail, rationale, Array.Empty<string>());

        return new Verdict(VerdictKind.Fail,
            $"malformed verdict from ratifier: '{raw}'",
            Array.Empty<string>());
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString();
        return val?.ToString();
    }
}
