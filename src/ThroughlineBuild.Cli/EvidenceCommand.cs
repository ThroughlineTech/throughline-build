using System.Text;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Plane;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Argument, formatting, and mutation boundary for <c>build evidence add</c>.
///
/// The command deliberately owns only one mutation: CreateCommentAsync. The subsequent
/// GetCommentsAsync call is a read-back check and lifecycle transitions remain separate verbs.
/// </summary>
internal static class EvidenceCommand
{
    internal const string Usage =
        "Usage: build evidence add --ticket <id> --kind <claim|review|commit|integrate|gate|final> [fields] [--json]";

    private static readonly string[] SupportedKinds =
        ["claim", "review", "commit", "integrate", "gate", "final"];

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--ticket",
        "--kind",
        "--claim",
        "--candidate-sha",
        "--run-head-sha",
        "--verdict",
        "--gate-result",
        "--fingerprint",
        "--summary",
        "--note"
    };

    private sealed record EvidenceInput(
        string Ticket,
        string Kind,
        string? Claim,
        string? CandidateSha,
        string? RunHeadSha,
        string? Verdict,
        string? GateResult,
        string? Fingerprint,
        string? Summary,
        string? Note);

    public static async Task<int> ExecuteAsync(
        string[] args,
        bool jsonOutput,
        ITicketing ticketing,
        TextWriter output,
        TextWriter error,
        CancellationToken ct,
        Func<PlaneApiException, string>? planeMessageFactory = null)
    {
        var parsed = Parse(args);
        if (parsed.Error is not null)
        {
            WriteUsageError(output, error, jsonOutput, parsed.Error);
            return 2;
        }

        var input = parsed.Input!;
        var markdown = FormatMarkdown(input);
        string commentId;

        try
        {
            // This is the only write in this command. In particular, a read-back failure must
            // never cause this call to be repeated because the server may already have stored it.
            commentId = await ticketing.CreateCommentAsync(
                input.Ticket, MarkdownHtml.Render(markdown), ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return WriteFailure(output, error, jsonOutput, CliErrorCodes.Usage, ex.Message, 2);
        }
        catch (KeyNotFoundException ex)
        {
            return WriteFailure(output, error, jsonOutput, CliErrorCodes.NotFound, ex.Message, 1);
        }
        catch (PlaneApiException ex)
        {
            var message = planeMessageFactory?.Invoke(ex) ?? ex.Message;
            return WriteFailure(output, error, jsonOutput,
                ex.Status == 404 ? CliErrorCodes.ConfigError : CliErrorCodes.Failure,
                message, 1);
        }
        catch (TicketingUnavailableException ex)
        {
            return WriteFailure(output, error, jsonOutput, CliErrorCodes.Failure, ex.Message, 1);
        }

        if (string.IsNullOrWhiteSpace(commentId))
        {
            return WriteFailure(
                output,
                error,
                jsonOutput,
                CliErrorCodes.Failure,
                "comment post returned no comment id; the comment may already exist; do not retry without checking comments",
                1);
        }

        IReadOnlyList<TicketComment> comments;
        try
        {
            comments = await ticketing.GetCommentsAsync(input.Ticket, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return WriteReadBackFailure(output, error, jsonOutput, commentId, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return WriteReadBackFailure(output, error, jsonOutput, commentId, ex.Message);
        }
        catch (PlaneApiException ex)
        {
            var message = planeMessageFactory?.Invoke(ex) ?? ex.Message;
            return WriteReadBackFailure(output, error, jsonOutput, commentId, message);
        }
        catch (TicketingUnavailableException ex)
        {
            return WriteReadBackFailure(output, error, jsonOutput, commentId, ex.Message);
        }

        var readBack = comments.FirstOrDefault(c =>
            string.Equals(c.Id, commentId, StringComparison.Ordinal));
        if (readBack is null)
        {
            return WriteReadBackFailure(
                output,
                error,
                jsonOutput,
                commentId,
                "the created comment was not present in the read-back comment list");
        }

        var evidence = new EvidenceView(
            input.Ticket,
            input.Kind,
            readBack.Id,
            HtmlToText.Render(readBack.Body),
            readBack.CreatedAt,
            ReadBackVerified: true);

        if (jsonOutput)
            CliEnvelopeWriter.WriteEvidence(output, evidence);
        else
        {
            output.WriteLine($"Added {input.Kind} evidence to {input.Ticket} (comment {readBack.Id})");
            output.WriteLine("Read-back verified.");
        }

        return 0;
    }

    internal static string FormatMarkdownForTest(
        string kind,
        string? claim = null,
        string? candidateSha = null,
        string? runHeadSha = null,
        string? verdict = null,
        string? gateResult = null,
        string? fingerprint = null,
        string? summary = null,
        string? note = null)
    {
        var parsed = Parse([
            "evidence", "add", "--ticket", "TLB-TEST", "--kind", kind,
            .. Optional("--claim", claim),
            .. Optional("--candidate-sha", candidateSha),
            .. Optional("--run-head-sha", runHeadSha),
            .. Optional("--verdict", verdict),
            .. Optional("--gate-result", gateResult),
            .. Optional("--fingerprint", fingerprint),
            .. Optional("--summary", summary),
            .. Optional("--note", note)
        ]);

        if (parsed.Error is not null)
            throw new ArgumentException(parsed.Error);
        return FormatMarkdown(parsed.Input!);
    }

    private static IEnumerable<string> Optional(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            yield return name;
            yield return value;
        }
    }

    private static (EvidenceInput? Input, string? Error) Parse(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[1], "add", StringComparison.Ordinal))
            return (null, "evidence add subcommand is required");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 2; i < args.Length; i++)
        {
            var option = args[i];
            if (!ValueOptions.Contains(option))
                return (null, $"unknown or misplaced evidence option '{option}'");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return (null, $"{option} requires a value");
            if (!values.TryAdd(option, args[++i]))
                return (null, $"{option} may be specified only once");
        }

        if (!values.TryGetValue("--ticket", out var ticket) || string.IsNullOrWhiteSpace(ticket))
            return (null, "--ticket is required");
        if (!values.TryGetValue("--kind", out var rawKind) || string.IsNullOrWhiteSpace(rawKind))
            return (null, "--kind is required");

        var kind = rawKind.Trim().ToLowerInvariant();
        if (!SupportedKinds.Contains(kind, StringComparer.Ordinal))
            return (null, $"invalid evidence kind '{rawKind}'; valid kinds: {string.Join(", ", SupportedKinds)}");

        foreach (var pair in values)
        {
            if (pair.Value.Contains('\r') || pair.Value.Contains('\n'))
                return (null, $"{pair.Key} must be a single line");
        }

        var input = new EvidenceInput(
            ticket.Trim(),
            kind,
            Get(values, "--claim"),
            Get(values, "--candidate-sha"),
            Get(values, "--run-head-sha"),
            Get(values, "--verdict"),
            Get(values, "--gate-result"),
            Get(values, "--fingerprint"),
            Get(values, "--summary"),
            Get(values, "--note"));

        var missing = new List<string>();
        if (kind == "claim") Require(input.Claim, "--claim", missing);
        if (kind is "claim" or "commit" or "integrate" or "final")
            Require(input.CandidateSha, "--candidate-sha", missing);
        if (kind is "review" or "integrate" or "gate" or "final")
            Require(input.RunHeadSha, "--run-head-sha", missing);
        if (kind == "review") Require(input.Verdict, "--verdict", missing);
        if (kind == "gate") Require(input.GateResult, "--gate-result", missing);
        if (kind is "claim" or "review" or "commit" or "integrate" or "gate" or "final")
            Require(input.Fingerprint, "--fingerprint", missing);

        if (missing.Count > 0)
            return (null, $"missing required field(s) for {kind}: {string.Join(", ", missing)}");

        var normalizedVerdict = input.Verdict;
        if (kind == "review")
        {
            if (!TryNormalizeVerdict(input.Verdict!, out var reviewVerdict))
                return (null, "--verdict must be Pass, Rework, or Fail");
            normalizedVerdict = reviewVerdict;
        }

        var normalizedGateResult = input.GateResult;
        if (kind == "gate")
        {
            if (!TryNormalizeGateResult(input.GateResult!, out var gateResult))
                return (null, "--gate-result must be pass, fail, or inconclusive");
            normalizedGateResult = gateResult;
        }

        return (input with
        {
            Verdict = normalizedVerdict,
            GateResult = normalizedGateResult
        }, null);
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static void Require(string? value, string name, List<string> missing)
    {
        if (string.IsNullOrWhiteSpace(value)) missing.Add(name);
    }

    private static bool TryNormalizeVerdict(string value, out string normalized)
    {
        normalized = value.Trim().ToLowerInvariant() switch
        {
            "pass" => "Pass",
            "rework" => "Rework",
            "fail" => "Fail",
            _ => string.Empty
        };
        return normalized.Length > 0;
    }

    private static bool TryNormalizeGateResult(string value, out string normalized)
    {
        normalized = value.Trim().ToLowerInvariant() switch
        {
            "pass" or "passed" or "success" => "pass",
            "fail" or "failed" or "failure" => "fail",
            "inconclusive" => "inconclusive",
            _ => string.Empty
        };
        return normalized.Length > 0;
    }

    private static string FormatMarkdown(EvidenceInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Build evidence: {input.Kind}");

        if (input.Claim is not null) AddText(sb, "claim", input.Claim);
        if (input.CandidateSha is not null) AddCode(sb, "candidate_sha", input.CandidateSha);
        if (input.RunHeadSha is not null) AddCode(sb, "run_head_sha", input.RunHeadSha);
        if (input.Verdict is not null) AddCode(sb, "verdict", input.Verdict);
        if (input.GateResult is not null) AddCode(sb, "gate_result", input.GateResult);
        if (input.Fingerprint is not null) AddCode(sb, "fingerprint", input.Fingerprint);
        if (input.Summary is not null) AddText(sb, "summary", input.Summary);
        if (input.Note is not null) AddText(sb, "note", input.Note);

        return sb.ToString().TrimEnd();
    }

    private static void AddText(StringBuilder sb, string name, string value) =>
        sb.Append("- ").Append(name).Append(": ").AppendLine(value);

    private static void AddCode(StringBuilder sb, string name, string value) =>
        sb.Append("- ").Append(name).Append(": `").Append(value.Replace("`", "\\`", StringComparison.Ordinal)).AppendLine("`");

    private static void WriteUsageError(TextWriter output, TextWriter error, bool jsonOutput, string message)
    {
        if (jsonOutput)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine(Usage);
        }
    }

    private static int WriteFailure(
        TextWriter output,
        TextWriter error,
        bool jsonOutput,
        string code,
        string message,
        int exitCode)
    {
        if (jsonOutput)
            CliEnvelopeWriter.WriteError(output, code, message);
        else
            error.WriteLine($"Command 'evidence add' failed: {message}");
        return exitCode;
    }

    private static int WriteReadBackFailure(
        TextWriter output,
        TextWriter error,
        bool jsonOutput,
        string commentId,
        string detail) =>
        WriteFailure(
            output,
            error,
            jsonOutput,
            CliErrorCodes.Failure,
            $"comment {commentId} was posted, but read-back failed: {detail}; do not retry without checking comments",
            1);
}
