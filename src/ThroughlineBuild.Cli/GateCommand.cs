using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Cli;

public static class GateCommand
{
    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        bool json,
        IReadOnlyList<CheckSpec> configuredChecks,
        string workingDirectory,
        AutomatedChecksRunner runner,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        var parsed = Parse(args);
        if (parsed.Error is not null)
            return Usage(json, output, error, parsed.Error);

        var selected = SelectChecks(configuredChecks, parsed.Role);
        IReadOnlyList<CheckResult> results;
        if (selected.Count == 0)
        {
            results = Array.Empty<CheckResult>();
        }
        else
        {
            results = await runner.RunAsync(
                selected,
                workingDirectory,
                ct,
                AutomatedChecksRunner.RequiredPathHandling.Inconclusive).ConfigureAwait(false);
        }

        var hasSelectedChecks = selected.Count > 0;
        var passed = hasSelectedChecks
            ? results.All(IsNonBlocking)
            : !parsed.RequireChecks;
        var message = !hasSelectedChecks && parsed.RequireChecks
            ? "no checks configured or selected"
            : configuredChecks.Count == 0
                ? "no checks configured"
                : selected.Count == 0
                    ? $"no checks selected for role {parsed.Role}"
                    : passed ? "gate passed" : "gate failed";

        var view = new GateView(
            parsed.Ticket,
            parsed.Role,
            Path.GetFullPath(workingDirectory),
            configuredChecks.Count > 0,
            passed,
            message,
            results.Select(ToView).ToList());

        if (json)
            CliEnvelopeWriter.WriteGate(output, view);
        else
            WriteHuman(output, view);

        return passed ? 0 : 1;
    }

    private static bool IsNonBlocking(CheckResult result) =>
        result.Role == CheckRole.Advisory
        || (result.Passed && !result.Inconclusive && !result.Skipped);

    private static IReadOnlyList<CheckSpec> SelectChecks(
        IReadOnlyList<CheckSpec> checks,
        string role)
    {
        return checks.Where(check => role switch
        {
            "gating" => check.Role is CheckRole.Setup or CheckRole.Gating,
            "advisory" => check.Role is CheckRole.Setup or CheckRole.Advisory,
            _ => true,
        }).ToList();
    }

    private static GateCheckView ToView(CheckResult result) => new(
        result.Name,
        result.Role.ToString().ToLowerInvariant(),
        Status(result),
        result.ExitCode,
        (long)Math.Round(result.Elapsed.TotalMilliseconds),
        result.StdoutTail,
        result.StderrTail,
        result.MissingRequiredPaths ?? Array.Empty<string>());

    private static string Status(CheckResult result)
    {
        if (result.Inconclusive)
            return "inconclusive";
        if (result.Skipped)
            return "not_run";
        return result.Passed ? "passed" : "failed";
    }

    private static void WriteHuman(TextWriter output, GateView view)
    {
        output.WriteLine(view.Message);
        if (view.Ticket is not null)
            output.WriteLine($"ticket: {view.Ticket}");
        output.WriteLine($"role: {view.Role}");
        output.WriteLine($"working directory: {view.WorkingDirectory}");
        foreach (var check in view.Checks)
        {
            output.WriteLine(
                $"[{check.Status.ToUpperInvariant()}] {check.Name} " +
                $"({check.Role}, exit {check.ExitCode}, {check.DurationMilliseconds} ms)");
            if (check.Stdout.Length > 0)
                output.WriteLine(check.Stdout);
            if (check.Stderr.Length > 0)
                output.WriteLine(check.Stderr);
        }
    }

    private static ParsedGateOptions Parse(IReadOnlyList<string> args)
    {
        string? ticket = null;
        var role = "all";
        var requireChecks = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (option is not "--ticket" and not "--role" and not "--require-checks")
                return new ParsedGateOptions(ticket, role, $"unknown option: {option}");
            if (!seen.Add(option))
                return new ParsedGateOptions(ticket, role, $"option may be specified only once: {option}");

            if (option == "--require-checks")
            {
                requireChecks = true;
                continue;
            }

            if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                return new ParsedGateOptions(ticket, role, $"{option} requires a value");

            if (option == "--ticket")
                ticket = args[i];
            else
                role = args[i].ToLowerInvariant();
        }

        if (role is not "gating" and not "advisory" and not "all")
            return new ParsedGateOptions(ticket, role, "--role must be gating, advisory, or all");

        return new ParsedGateOptions(ticket, role, requireChecks, null);
    }

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build gate [--ticket <id>] [--role gating|advisory|all] [--require-checks] [--json]");
        }
        return 2;
    }

    private sealed record ParsedGateOptions(string? Ticket, string Role, bool RequireChecks, string? Error)
    {
        public ParsedGateOptions(string? ticket, string role, string? error)
            : this(ticket, role, false, error)
        {
        }
    }
}
