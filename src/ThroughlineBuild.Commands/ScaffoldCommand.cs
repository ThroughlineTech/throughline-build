using ThroughlineBuild.Contracts;
using ThroughlineBuild.Scaffold;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Exit-category tag embedded in CommandResult.Message to let Program.cs
/// map the result to the correct process exit code.
/// Format: first line is the tag, remainder is the human-readable output.
/// </summary>
public static class ScaffoldExitCategory
{
    public const string Clean = "EXIT:0";
    public const string ValidationError = "EXIT:2";
    public const string PartialCreation = "EXIT:3";
    public const string BackendUnavailable = "EXIT:4";
}

/// <summary>
/// Implements ITicketCommand for the 'build scaffold' verb.
/// Reads op_doc_path / validate_only / dry_run / accept_warnings from ctx.Args,
/// constructs ScaffoldOptions, invokes ScaffoldPhase.RunAsync, and returns a
/// CommandResult carrying formatted output and an exit-category tag.
/// </summary>
public sealed class ScaffoldCommand : ITicketCommand
{
    private const string OpDocSpecHint = "See 'build op-doc spec' for the authoring rules.";

    private readonly ScaffoldPhase _phase;

    public ScaffoldCommand(ScaffoldPhase phase)
    {
        _phase = phase;
    }

    public async Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
    {
        // Extract required op_doc_path.
        if (!ctx.Args.TryGetValue("op_doc_path", out var opDocPath) || string.IsNullOrWhiteSpace(opDocPath))
        {
            return new CommandResult(false, $"{ScaffoldExitCategory.ValidationError}\nop_doc_path is required");
        }

        bool validateOnly = ctx.Args.TryGetValue("validate_only", out var vOnly) && vOnly == "true";
        bool dryRun = ctx.Args.TryGetValue("dry_run", out var dRun) && dRun == "true";
        bool acceptWarnings = ctx.Args.TryGetValue("accept_warnings", out var aWarn) && aWarn == "true";
        bool showLocation = ctx.Args.TryGetValue("show_location", out var sLoc) && sLoc == "true";

        // --validate-only: parse + validate, no creation, print errors/warnings and exit.
        if (validateOnly)
        {
            return RunValidateOnly(opDocPath, showLocation);
        }

        var options = new ScaffoldOptions(
            OpDocPath: opDocPath,
            DryRun: dryRun,
            AcceptWarnings: acceptWarnings);

        ScaffoldResult result;
        try
        {
            result = await _phase.RunAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, $"{ScaffoldExitCategory.ValidationError}\nCancelled.");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, showLocation
                ? $"scaffold failed: {ex.Message}\n{ex.StackTrace?.Split('\n')[0].Trim() ?? string.Empty}"
                : $"scaffold failed: {ex.Message}");
        }

        if (result.WasAbortedByParseErrors)
        {
            // Parse errors: surface via validate-only path for detailed output.
            var parseOnlyResult = RunValidateOnly(opDocPath, showLocation);
            return new CommandResult(false, $"{ScaffoldExitCategory.ValidationError}\n{StripTag(parseOnlyResult.Message)}");
        }

        if (result.WasAbortedByValidationErrors)
        {
            var valOnlyResult = RunValidateOnly(opDocPath, showLocation);
            return new CommandResult(false, $"{ScaffoldExitCategory.ValidationError}\n{StripTag(valOnlyResult.Message)}");
        }

        if (result.WasBlockedByWarnings)
        {
            // Blocked by warnings: show warnings, tell operator to use --accept-warnings.
            var warnResult = RunValidateOnly(opDocPath, showLocation);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(StripTag(warnResult.Message));
            sb.AppendLine("Re-run with --accept-warnings to proceed past warnings.");
            sb.AppendLine(OpDocSpecHint);
            return new CommandResult(false, $"{ScaffoldExitCategory.ValidationError}\n{sb.ToString().TrimEnd()}");
        }

        if (result.WasDryRun)
        {
            return BuildDryRunOutput(opDocPath, result);
        }

        return BuildCreateOutput(opDocPath, result);
    }

    // --- validate-only path: no ScaffoldPhase invocation ---
    private static CommandResult RunValidateOnly(string opDocPath, bool showLocation = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Validating {opDocPath} ...");

        var parseResult = OpDocParser.Parse(opDocPath);

        var hardErrors = parseResult.Errors
            .Where(e => !e.Message.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hardErrors.Count > 0 || parseResult.Parsed == null)
        {
            sb.AppendLine("Errors:");
            foreach (var err in hardErrors)
            {
                sb.AppendLine($"  [PARSE] line {err.LineNumber} ({err.Section}): {err.Message}");
                if (showLocation && err.SourceFile != null)
                    sb.AppendLine($"         at {err.SourceMember} ({Path.GetFileName(err.SourceFile)}:{err.SourceLineNumber})");
            }
            if (!hardErrors.Any() && parseResult.Parsed == null)
            {
                sb.AppendLine("  [PARSE] could not parse op-doc: document structure is invalid.");
            }
            AppendOpDocSpecHint(sb);
            return new CommandResult(false, $"{ScaffoldExitCategory.ValidationError}\n{sb.ToString().TrimEnd()}");
        }

        var validationResult = OpDocValidator.Validate(parseResult.Parsed!);

        bool hasErrors = !validationResult.IsValid;
        bool hasWarnings = validationResult.Warnings.Count > 0;

        if (hasErrors)
        {
            sb.AppendLine("Errors:");
            foreach (var err in validationResult.Errors)
            {
                sb.AppendLine($"  [{err.Code}] {err.Path}: {err.Message}");
            }
        }
        if (hasWarnings)
        {
            sb.AppendLine("Warnings:");
            foreach (var warn in validationResult.Warnings)
            {
                sb.AppendLine($"  [{warn.Code}] {warn.Path}: {warn.Message}");
            }
        }

        if (!hasErrors && !hasWarnings)
        {
            sb.AppendLine("OK: no errors or warnings.");
        }

        if (hasErrors)
        {
            AppendOpDocSpecHint(sb);
        }

        bool success = !hasErrors;
        string tag = hasErrors ? ScaffoldExitCategory.ValidationError : ScaffoldExitCategory.Clean;
        return new CommandResult(success, $"{tag}\n{sb.ToString().TrimEnd()}");
    }

    // --- dry-run output ---
    private static CommandResult BuildDryRunOutput(string opDocPath, ScaffoldResult result)
    {
        // Re-parse to get plan/brief details for the preview tree.
        var parseResult = OpDocParser.Parse(opDocPath);
        if (parseResult.Parsed == null)
        {
            return new CommandResult(false, $"{ScaffoldExitCategory.ValidationError}\nCould not re-parse op-doc for dry-run output.");
        }

        var opDoc = parseResult.Parsed;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Would create 1 operation + {result.PlansCreated} plan(s) + {result.BriefsCreated} brief(s):");
        sb.AppendLine($"  [Op] \"{opDoc.Title}\" (plan-ticket)");

        foreach (var entry in opDoc.DispatchOrder)
        {
            var plan = opDoc.Plans.FirstOrDefault(p => p.Id == entry.PlanId);
            if (plan == null) continue;

            sb.AppendLine($"    [Plan {plan.Id}] \"Plan {plan.Id}: {plan.Name}\" (plan-ticket)");
            foreach (var brief in plan.Briefs)
            {
                sb.AppendLine($"      -> [Brief {plan.Id}.{brief.Number:D2}] \"{brief.Slug}\"");
            }
        }

        return new CommandResult(true, $"{ScaffoldExitCategory.Clean}\n{sb.ToString().TrimEnd()}");
    }

    // --- real creation output ---
    private static CommandResult BuildCreateOutput(string opDocPath, ScaffoldResult result)
    {
        // Re-parse to correlate ticket IDs with plan/brief names.
        var parseResult = OpDocParser.Parse(opDocPath);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Scaffolding {opDocPath} ...");

        if (parseResult.Parsed != null)
        {
            var opDoc = parseResult.Parsed;

            // CreatedTicketIds is [op, plan, briefs..., plan, briefs...]. The op id lives
            // at index 0 whenever the op was created (OpTicketId set), and it is printed
            // separately below - so plan/brief consumption must start *after* it. Starting
            // at 0 reprinted the op id as the first plan and shifted every id down one,
            // dropping the last brief.
            int idIndex = string.IsNullOrEmpty(result.OpTicketId) ? 0 : 1;

            // Emit operation ticket
            if (!string.IsNullOrEmpty(result.OpTicketId))
            {
                sb.AppendLine($"Created operation ticket: {result.OpTicketId} \"{opDoc.Title}\"");
            }

            foreach (var entry in opDoc.DispatchOrder)
            {
                var plan = opDoc.Plans.FirstOrDefault(p => p.Id == entry.PlanId);
                if (plan == null) continue;

                string planId = idIndex < result.CreatedTicketIds.Count
                    ? result.CreatedTicketIds[idIndex++]
                    : "?";
                sb.AppendLine($"Created plan {plan.Id}: {planId} \"Plan {plan.Id}: {plan.Name}\"");

                foreach (var brief in plan.Briefs)
                {
                    string briefId = idIndex < result.CreatedTicketIds.Count
                        ? result.CreatedTicketIds[idIndex++]
                        : "?";
                    sb.AppendLine($"  Created brief: {briefId} \"{brief.Slug}\" (parent: {planId})");
                }
            }
        }
        else
        {
            // Fallback if re-parse fails: just print counts.
            sb.AppendLine($"Created {result.PlansCreated} plan(s) and {result.BriefsCreated} brief(s).");
        }

        if (result.Failures.Count > 0)
        {
            sb.AppendLine("Failures:");
            foreach (var f in result.Failures)
            {
                sb.AppendLine($"  [{f.Stage}] {f.Detail}");
            }
        }

        bool isPartial = result.Failures.Count > 0 && (result.PlansCreated > 0 || result.BriefsCreated > 0);
        bool isFullFailure = result.Failures.Count > 0 && result.PlansCreated == 0 && result.BriefsCreated == 0;

        if (!isPartial && !isFullFailure)
        {
            sb.AppendLine($"Scaffold complete: {result.PlansCreated} plan(s), {result.BriefsCreated} brief(s) created.");
        }
        else if (isPartial)
        {
            sb.AppendLine($"Scaffold partial: {result.PlansCreated} plan(s), {result.BriefsCreated} brief(s) created with {result.Failures.Count} failure(s).");
        }

        string tag = isPartial
            ? ScaffoldExitCategory.PartialCreation
            : isFullFailure
                ? ScaffoldExitCategory.BackendUnavailable
                : ScaffoldExitCategory.Clean;
        bool success = !isPartial && !isFullFailure;
        return new CommandResult(success, $"{tag}\n{sb.ToString().TrimEnd()}");
    }

    private static string StripTag(string? message)
    {
        if (message == null) return string.Empty;
        var nl = message.IndexOf('\n');
        if (nl < 0) return message;
        return message.Substring(nl + 1);
    }

    private static void AppendOpDocSpecHint(System.Text.StringBuilder sb)
    {
        sb.AppendLine(OpDocSpecHint);
    }
}
