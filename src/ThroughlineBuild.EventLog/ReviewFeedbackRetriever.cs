using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.EventLog;

/// <summary>
/// Scans the JSONL event log for the most recent VerifierVerdict event for a given ticket
/// and reconstructs a ReviewFeedback record when the verdict was Rework.
/// Thread-safe: all scans are read-only; no instance state is mutated after construction.
/// </summary>
public sealed class ReviewFeedbackRetriever : IReviewFeedbackRetriever
{
    private readonly string _eventsDirectory;
    private static readonly int VerifierVerdictKind = (int)EventKind.VerifierVerdict;

    public ReviewFeedbackRetriever(string eventsDirectory)
    {
        _eventsDirectory = eventsDirectory;
    }

    /// <summary>
    /// Returns a ReviewFeedback if the most recent VerifierVerdict for <paramref name="ticketId"/>
    /// has verdict = "Rework". Returns null in all other cases:
    ///   - most recent verdict is Pass or Fail
    ///   - no VerifierVerdict event found for the ticket
    ///   - events directory does not exist
    /// Never throws; malformed JSONL lines and missing Data fields are skipped silently.
    /// </summary>
    public ReviewFeedback? GetLatestRework(string ticketId)
    {
        if (!Directory.Exists(_eventsDirectory))
            return null;

        // Order files by last-write time descending; newest session first.
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_eventsDirectory, "*.jsonl")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f));
        }
        catch
        {
            return null;
        }

        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue;
            }

            // Iterate lines in reverse-chronological order (newest events last in file).
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ReviewFeedback? result = TryParseReworkFeedback(line, ticketId);
                if (result == _sentinel)
                {
                    // Most recent review found; verdict is not Rework.
                    return null;
                }
                if (result != null)
                {
                    return result;
                }
                // null (without sentinel) means line was not a matching VerifierVerdict - keep scanning.
            }
        }

        return null;
    }

    // Sentinel value used to signal "found the most recent review for this ticket but it
    // was not a Rework verdict" so GetLatestRework can return null without continuing to scan.
    private static readonly ReviewFeedback _sentinel = new ReviewFeedback("__sentinel__", Array.Empty<string>(), 0);

    /// <summary>
    /// Parses one JSONL line and returns:
    ///   - A valid ReviewFeedback if the line is a Rework VerifierVerdict for the ticket.
    ///   - _sentinel if the line is a non-Rework VerifierVerdict for the ticket (stop scanning).
    ///   - null if the line is not a matching VerifierVerdict (skip, keep scanning).
    /// </summary>
    private static ReviewFeedback? TryParseReworkFeedback(string line, string ticketId)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Must be a VerifierVerdict event (Kind integer == 3).
            if (!root.TryGetProperty("Kind", out var kindProp) ||
                kindProp.ValueKind != JsonValueKind.Number ||
                kindProp.GetInt32() != VerifierVerdictKind)
            {
                return null;
            }

            // Must match the requested ticket.
            if (!root.TryGetProperty("TicketId", out var ticketIdProp) ||
                ticketIdProp.GetString() != ticketId)
            {
                return null;
            }

            // Found a VerifierVerdict for this ticket. Now inspect the verdict.
            if (!root.TryGetProperty("Data", out var dataProp) ||
                dataProp.ValueKind != JsonValueKind.Object)
            {
                // Malformed - missing or non-object Data; skip and keep scanning.
                return null;
            }

            // The verdict field in Data is named "kind" (matches ReviewPhase emitting
            // ["kind"] = verdict.Kind.ToString() -- e.g. "Rework", "Pass", "Fail").
            if (!dataProp.TryGetProperty("kind", out var verdictProp) ||
                verdictProp.ValueKind != JsonValueKind.String)
            {
                // Malformed - missing kind field in Data; skip and keep scanning.
                return null;
            }

            var verdict = verdictProp.GetString();
            if (verdict != "Rework")
            {
                // Most recent review is Pass or Fail; contract says return null.
                return _sentinel;
            }

            // Verdict is Rework. Extract rationale (required) and checks_failed (optional).
            if (!dataProp.TryGetProperty("rationale", out var rationaleProp) ||
                rationaleProp.ValueKind != JsonValueKind.String)
            {
                // Missing rationale; treat as malformed, skip this event and continue.
                return null;
            }

            var rationale = rationaleProp.GetString() ?? string.Empty;

            var checksFailed = new List<string>();
            if (dataProp.TryGetProperty("checks_failed", out var checksFailedProp) &&
                checksFailedProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in checksFailedProp.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var s = element.GetString();
                        if (s != null)
                            checksFailed.Add(s);
                    }
                }
            }

            var failedCheckDetails = ParseFailedCheckDetails(dataProp);

            return new ReviewFeedback(rationale, checksFailed, ReworkRoundNumber: 1,
                FailedCheckDetails: failedCheckDetails);
        }
    }

    // Reconstructs the cited failing checks' raw evidence (command, exit code, output tails)
    // persisted by ReviewPhase under "checks_failed_details", so a rework resumed from the
    // event log briefs the worker with the check's own output - not just names plus the
    // reviewer's prose theory of the tool's rules. Absent/malformed entries degrade to null
    // (names-only feedback), never to a parse failure: events written before this key existed
    // must keep resuming.
    private static IReadOnlyList<CheckResult>? ParseFailedCheckDetails(JsonElement dataProp)
    {
        if (!dataProp.TryGetProperty("checks_failed_details", out var detailsProp) ||
            detailsProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var details = new List<CheckResult>();
        foreach (var element in detailsProp.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetStringOrNull(element, "name");
            if (string.IsNullOrEmpty(name))
                continue;

            int exitCode = 0;
            if (element.TryGetProperty("exit_code", out var exitProp) &&
                exitProp.ValueKind == JsonValueKind.Number &&
                exitProp.TryGetInt32(out var parsedExit))
            {
                exitCode = parsedExit;
            }

            var role = CheckRole.Gating;
            var roleStr = GetStringOrNull(element, "role");
            if (roleStr is not null && Enum.TryParse<CheckRole>(roleStr, ignoreCase: true, out var parsedRole))
                role = parsedRole;

            details.Add(new CheckResult(
                Name: name,
                Passed: false,
                ExitCode: exitCode,
                StdoutTail: GetStringOrNull(element, "stdout_tail") ?? "",
                StderrTail: GetStringOrNull(element, "stderr_tail") ?? "",
                Elapsed: TimeSpan.Zero,
                Role: role,
                CommandLine: GetStringOrNull(element, "command") ?? ""));
        }

        return details.Count > 0 ? details : null;
    }

    private static string? GetStringOrNull(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
