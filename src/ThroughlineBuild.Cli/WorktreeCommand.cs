using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Cli;

public static class WorktreeCommand
{
    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        bool json,
        WorktreeLeaseManager manager,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (args.Count < 2)
            return Usage(json, output, error, "worktree subcommand is required");

        switch (args[1])
        {
            case "lease":
            {
                var parsed = ParseOptions(
                    args, 2, ["--ticket", "--slug", "--base", "--require-seed"]);
                if (parsed.Error is not null)
                    return Usage(json, output, error, parsed.Error);
                if (!parsed.Values.TryGetValue("--ticket", out var ticket))
                    return Usage(json, output, error, "--ticket is required for worktree lease");

                parsed.Values.TryGetValue("--slug", out var slug);
                parsed.Values.TryGetValue("--base", out var baseRef);
                parsed.Values.TryGetValue("--require-seed", out var requiredSeed);
                var result = await manager.LeaseAsync(
                    new WorktreeLeaseRequest(ticket, slug, baseRef, requiredSeed), ct)
                    .ConfigureAwait(false);
                if (!result.Success)
                    return ReportFailure(result, json, output, error);

                if (json)
                    CliEnvelopeWriter.WriteWorktreeLease(output, result.Manifest!);
                else
                    output.WriteLine(result.Manifest!.WorktreePath);
                return 0;
            }
            case "teardown":
            {
                var parsed = ParseOptions(args, 2, ["--ticket", "--dir", "--require-merged-into"], ["--force"]);
                if (parsed.Error is not null)
                    return Usage(json, output, error, parsed.Error);
                var hasTicket = parsed.Values.TryGetValue("--ticket", out var ticket);
                var hasDir = parsed.Values.TryGetValue("--dir", out var dir);
                if (hasTicket == hasDir)
                    return Usage(json, output, error, "worktree teardown requires exactly one of --ticket or --dir");
                parsed.Values.TryGetValue("--require-merged-into", out var requireMergedInto);
                if (requireMergedInto is not null && string.IsNullOrWhiteSpace(requireMergedInto))
                    return Usage(json, output, error, "--require-merged-into requires a non-empty ref");

                var result = await manager.TeardownAsync(
                    ticket,
                    dir,
                    ct,
                    force: parsed.Flags.Contains("--force"),
                    requireMergedInto: requireMergedInto).ConfigureAwait(false);
                if (!result.Success)
                    return ReportFailure(result, json, output, error);
                if (json)
                    CliEnvelopeWriter.WriteWorktreeTeardown(output, result.Manifest!);
                else
                    output.WriteLine(result.Manifest!.WorktreePath);
                return 0;
            }
            case "list":
            {
                if (args.Count != 2)
                    return Usage(json, output, error, $"unknown argument for worktree list: {args[2]}");
                var result = await manager.ListAsync(ct).ConfigureAwait(false);
                if (json)
                {
                    CliEnvelopeWriter.WriteWorktreeList(output, result);
                }
                else
                {
                    output.WriteLine("Leases:");
                    foreach (var lease in result.Leases)
                        output.WriteLine($"  {lease.Ticket}  {lease.Branch}  {lease.WorktreePath}");
                    output.WriteLine("Unmanifested directories:");
                    foreach (var dir in result.UnmanifestedDirectories)
                        output.WriteLine($"  {dir}");
                }
                return 0;
            }
            default:
                return Usage(json, output, error, $"unknown worktree subcommand: {args[1]}");
        }
    }

    private static ParsedOptions ParseOptions(
        IReadOnlyList<string> args,
        int start,
        IReadOnlyCollection<string> allowed) =>
        ParseOptions(args, start, allowed, Array.Empty<string>());

    private static ParsedOptions ParseOptions(
        IReadOnlyList<string> args,
        int start,
        IReadOnlyCollection<string> allowedValues,
        IReadOnlyCollection<string> allowedFlags)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = start; i < args.Count; i++)
        {
            var option = args[i];
            if (allowedFlags.Contains(option))
            {
                if (!flags.Add(option))
                    return new ParsedOptions(values, flags, $"option may be specified only once: {option}");
                continue;
            }
            if (!allowedValues.Contains(option))
                return new ParsedOptions(values, flags, $"unknown option: {option}");
            if (values.ContainsKey(option))
                return new ParsedOptions(values, flags, $"option may be specified only once: {option}");
            if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                return new ParsedOptions(values, flags, $"{option} requires a value");
            values[option] = args[i];
        }
        return new ParsedOptions(values, flags, null);
    }

    private static int ReportFailure(
        WorktreeLeaseResult result,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, result.ErrorCode!, result.Message!);
        else
            error.WriteLine($"Worktree error: {result.Message}");
        return result.ErrorCode switch
        {
            WorktreeLeaseManager.CollisionError => 6,
            WorktreeLeaseManager.MissingSeedError => 7,
            WorktreeLeaseManager.InvalidManifestError or
            WorktreeLeaseManager.ContainmentError => 8,
            _ => 1,
        };
    }

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build worktree lease --ticket <id> [--slug <slug>] [--base <ref>] [--require-seed <path>]");
            error.WriteLine("       build worktree teardown (--ticket <id> | --dir <path>) [--require-merged-into <ref>] [--force]");
            error.WriteLine("       build worktree list");
            error.WriteLine("       default teardown refuses tracked work and unexpected untracked files");
            error.WriteLine("       --require-merged-into proves the helper branch is an ancestor of the named ref before removal");
            error.WriteLine("       --force skips those checks and may permanently discard work");
        }
        return 2;
    }

    private sealed record ParsedOptions(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> Flags,
        string? Error);
}
