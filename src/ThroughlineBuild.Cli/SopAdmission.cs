using System.Diagnostics;
using System.Text.RegularExpressions;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Commands;

namespace ThroughlineBuild.Cli;

internal static partial class SopAdmission
{
    public const string ModeName = "admission";
    public const string StandardModeName = "standard";
    public const string RunModeEnvironmentVariable = "BUILD_SOP_RUN_MODE";
    public const string InspectionShaEnvironmentVariable = "BUILD_SOP_INSPECTION_SHA";
    public const string InspectionRootEnvironmentVariable = "BUILD_SOP_INSPECTION_ROOT";

    public static readonly SopVerbPolicyView StandardPolicy = new(
        ReadOnlyVerbsAllowed: true,
        WorktreeLeaseAllowed: true,
        WorktreeTeardownAllowed: true,
        TicketTransitionAllowed: true,
        TicketCommentAllowed: true,
        CommitAllowed: true,
        BranchAllowed: true,
        PushAllowed: true,
        ParentOrEpicExpansionAllowed: true,
        AllowedBuildVerbs: ["all configured verbs"],
        RefusedBuildVerbs: []);

    public static readonly SopVerbPolicyView AdmissionPolicy = new(
        ReadOnlyVerbsAllowed: true,
        WorktreeLeaseAllowed: false,
        WorktreeTeardownAllowed: false,
        TicketTransitionAllowed: false,
        TicketCommentAllowed: false,
        CommitAllowed: false,
        BranchAllowed: false,
        PushAllowed: false,
        ParentOrEpicExpansionAllowed: false,
        AllowedBuildVerbs:
        [
            "build list",
            "build get",
            "build comments",
            "build candidate status",
            "build waves",
            "build worktree list",
            "build sop list",
            "build sop status",
            "build sop doctor",
            "build sop brief",
        ],
        RefusedBuildVerbs:
        [
            "build worktree lease",
            "build worktree teardown",
            "build comment",
            "build transition",
            "build new",
            "build amend",
            "build close",
            "build defer",
            "build reopen",
            "build relate",
            "build setup",
            "build sop install",
            "build sop upgrade",
            "build sop uninstall",
            "build plan",
            "build implement",
            "build review",
            "build rework",
            "build decompose",
            "build scaffold",
            "build ship",
            "build chain",
            "build sweep",
            "build gate",
            "build init",
            "build settarget",
            "build user-guide",
            "build op-doc",
            "build models refresh",
        ]);

    public static SopRunModeView StandardRunMode() => new(
        Mode: StandardModeName,
        InspectionSha: null,
        InspectionRoot: null,
        Environment: [],
        VerbPolicy: StandardPolicy);

    public static bool TryCreateAdmissionRunMode(
        string inspectionRoot,
        string inspectionSha,
        string invocationDirectory,
        out SopRunModeView runMode,
        out string error)
    {
        runMode = StandardRunMode();
        error = string.Empty;

        if (!Path.IsPathFullyQualified(inspectionRoot))
        {
            error = "admission inspection root must be an absolute path";
            return false;
        }

        var fullRoot = Path.GetFullPath(inspectionRoot);
        if (!FullShaRegex().IsMatch(inspectionSha))
        {
            error = "admission inspection SHA must be a full 40-character hexadecimal commit SHA";
            return false;
        }

        if (!Directory.Exists(fullRoot))
        {
            error = $"admission inspection root does not exist: {fullRoot}";
            return false;
        }

        if (!TryResolveGitWorktree(fullRoot, out var inspectionIdentity, out var inspectionError))
        {
            error = $"admission inspection root is not a git worktree: {inspectionError}";
            return false;
        }

        var resolvedRoot = inspectionIdentity.TopLevel;
        if (!PathsEqual(fullRoot, resolvedRoot))
        {
            error = $"admission inspection root must be the git worktree root, not a subdirectory: {fullRoot}";
            return false;
        }

        if (!TryResolveGitWorktree(invocationDirectory, out var invocationIdentity, out var invocationError))
        {
            error = $"admission mode must be invoked from a git worktree: {invocationError}";
            return false;
        }

        if (!PathsEqual(inspectionIdentity.CommonDirectory, invocationIdentity.CommonDirectory))
        {
            error = "admission inspection root must belong to the invoking repository; cross-repository admission is not supported";
            return false;
        }

        var commit = RunGit(resolvedRoot, "rev-parse", "--verify", $"{inspectionSha}^{{commit}}");
        if (commit.ExitCode != 0)
        {
            error = $"admission inspection SHA does not resolve to a commit: {inspectionSha}";
            return false;
        }

        var normalizedSha = commit.Stdout.Trim().ToLowerInvariant();
        if (!FullShaRegex().IsMatch(normalizedSha))
        {
            error = $"admission inspection SHA did not resolve to a full commit SHA: {inspectionSha}";
            return false;
        }

        runMode = new SopRunModeView(
            Mode: ModeName,
            InspectionSha: normalizedSha,
            InspectionRoot: resolvedRoot,
            Environment:
            [
                new SopEnvironmentVariableView(RunModeEnvironmentVariable, ModeName),
                new SopEnvironmentVariableView(InspectionShaEnvironmentVariable, normalizedSha),
                new SopEnvironmentVariableView(InspectionRootEnvironmentVariable, resolvedRoot),
            ],
            VerbPolicy: AdmissionPolicy);
        return true;
    }

    public static bool TryCreateAdmissionRunModeFromEnvironment(
        string invocationDirectory,
        out SopRunModeView runMode,
        out string error)
    {
        runMode = StandardRunMode();
        error = string.Empty;

        var root = Environment.GetEnvironmentVariable(InspectionRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            error = $"active SOP admission mode requires {InspectionRootEnvironmentVariable}";
            return false;
        }

        var sha = Environment.GetEnvironmentVariable(InspectionShaEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(sha))
        {
            error = $"active SOP admission mode requires {InspectionShaEnvironmentVariable}";
            return false;
        }

        return TryCreateAdmissionRunMode(root, sha, invocationDirectory, out runMode, out error);
    }

    public static bool IsActiveFromEnvironment() =>
        string.Equals(
            Environment.GetEnvironmentVariable(RunModeEnvironmentVariable),
            ModeName,
            StringComparison.Ordinal);

    public static bool TryGetRefusal(
        IReadOnlyList<string> args,
        ICliVerb? verb,
        out SopAdmissionRefusal refusal)
    {
        refusal = default;
        if (verb is null)
            return false;

        if (!IsActiveFromEnvironment())
            return false;

        switch (verb.Kind)
        {
            case CliVerbKind.List:
            case CliVerbKind.Get:
            case CliVerbKind.Comments:
            case CliVerbKind.Candidate:
            case CliVerbKind.Waves:
                return false;
            case CliVerbKind.Worktree:
                if (args.Count >= 2 && string.Equals(args[1], "list", StringComparison.Ordinal))
                    return false;
                if (args.Count >= 2 && string.Equals(args[1], "teardown", StringComparison.Ordinal))
                    return Refuse("worktree_teardown", "worktree teardown is disabled by SOP admission mode", out refusal);
                return Refuse("worktree_lease", "worktree lease is disabled by SOP admission mode", out refusal);
            case CliVerbKind.Sop:
                if (args.Count >= 2 && IsAllowedSopSubcommand(args[1]))
                    return false;
                return Refuse("sop_mutation", "SOP install, upgrade, and uninstall are disabled by SOP admission mode", out refusal);
            case CliVerbKind.Comment:
                return Refuse("ticket_comment", "ticket comments are disabled by SOP admission mode", out refusal);
            case CliVerbKind.Transition:
                return Refuse("ticket_transition", "ticket transitions are disabled by SOP admission mode", out refusal);
            case CliVerbKind.Chain:
                return Refuse("scope_expansion", "parent and epic expansion are disabled by SOP admission mode", out refusal);
            case CliVerbKind.Ship:
            case CliVerbKind.Plan:
            case CliVerbKind.Implement:
            case CliVerbKind.Review:
            case CliVerbKind.Rework:
            case CliVerbKind.Decompose:
            case CliVerbKind.Scaffold:
                return Refuse("git_mutation", "commit, branch, and push operations are disabled by SOP admission mode", out refusal);
            default:
                return Refuse("mutation", $"build {verb.Name} is disabled by SOP admission mode", out refusal);
        }
    }

    public static int WriteRefusal(
        SopAdmissionRefusal refusal,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        var message = $"{refusal.Message} ({refusal.Category})";
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.SopAdmissionRefused, message);
        else
            error.WriteLine($"Error: {message}");
        return 1;
    }

    private static bool IsAllowedSopSubcommand(string subcommand) =>
        subcommand is "brief" or "doctor" or "list" or "status";

    private static bool Refuse(string category, string message, out SopAdmissionRefusal refusal)
    {
        refusal = new SopAdmissionRefusal(category, message);
        return true;
    }

    private static ProcessResult RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.Start();
        try { process.StandardInput.Close(); } catch { /* best effort */ }
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string TrimProcessError(ProcessResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout
            : result.Stderr;
        return message.Trim();
    }

    private static bool TryResolveGitWorktree(
        string workingDirectory,
        out GitWorktreeIdentity identity,
        out string error)
    {
        identity = default;
        var topLevel = RunGit(workingDirectory, "rev-parse", "--show-toplevel");
        if (topLevel.ExitCode != 0 || string.IsNullOrWhiteSpace(topLevel.Stdout))
        {
            error = TrimProcessError(topLevel);
            return false;
        }

        var commonDirectory = RunGit(workingDirectory, "rev-parse", "--git-common-dir");
        if (commonDirectory.ExitCode != 0 || string.IsNullOrWhiteSpace(commonDirectory.Stdout))
        {
            error = TrimProcessError(commonDirectory);
            return false;
        }

        identity = new GitWorktreeIdentity(
            Path.GetFullPath(topLevel.Stdout.Trim()),
            NormalizeGitPath(workingDirectory, commonDirectory.Stdout.Trim()));
        error = string.Empty;
        return true;
    }

    private static string NormalizeGitPath(string workingDirectory, string path)
    {
        var rooted = Path.IsPathFullyQualified(path)
            ? path
            : Path.Combine(workingDirectory, path);
        return TrimEndingSeparator(Path.GetFullPath(rooted));
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return comparer.Equals(
            TrimEndingSeparator(Path.GetFullPath(left)),
            TrimEndingSeparator(Path.GetFullPath(right)));
    }

    private static string TrimEndingSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(path);

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex FullShaRegex();

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);

    private readonly record struct GitWorktreeIdentity(string TopLevel, string CommonDirectory);
}

internal readonly record struct SopAdmissionRefusal(string Category, string Message);
