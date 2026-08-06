using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Scaffold;

namespace ThroughlineBuild.Cli;

internal sealed record InstallInvocation(string? ProfilePath, string? InvariantsPath, bool Force = false);
internal sealed record InstallProfileApplyResult(
    bool Success, int ExitCode, string Message, string? Language, string? Framework);

internal sealed record InstallDependencies(
    Func<string, TextReader, TextWriter, CancellationToken, Task<int>> EnsureInitAndSetup,
    Func<string, TextWriter, CancellationToken, Task<int>> InstallSop,
    Func<string, SopDoctorView> RunDoctor,
    Func<string, string, IReadOnlyList<string>, BuildConfig, CancellationToken, Task<InstallReadinessResult>> AssertReadiness);

public sealed record InstallView(
    string Stage,
    bool Ready,
    string Message,
    string? Prompt,
    string? NextCommand,
    string? Branch,
    string? Porcelain,
    IReadOnlyList<string> StubPaths);

public sealed record InstallEnvelope(int SchemaVersion, bool Ok, InstallView Data);

internal static class InstallCommand
{
    private const string RunBranch = "run/backlog";

    public static bool TryParse(IReadOnlyList<string> args, out InstallInvocation? invocation, out string error)
    {
        invocation = null;
        error = string.Empty;
        string? profile = null;
        string? invariants = null;
        var force = false;
        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (option == "--force")
            {
                if (force) { error = "--force may be specified only once"; return false; }
                force = true;
                continue;
            }
            if (option is not ("--profile" or "--invariants"))
            {
                error = $"unknown install option: {option}";
                return false;
            }
            if (++i >= args.Count || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{option} requires a path or '-'";
                return false;
            }
            if (option == "--profile")
            {
                if (profile is not null) { error = "--profile may be specified only once"; return false; }
                profile = args[i];
            }
            else
            {
                if (invariants is not null) { error = "--invariants may be specified only once"; return false; }
                invariants = args[i];
            }
        }
        if (profile is not null && invariants is not null)
        {
            error = "install accepts either --profile or --invariants, not both";
            return false;
        }
        // --force is the override the profile-apply refusal names, so it has to be reachable from the
        // command that prints it - but it only means anything to the profile stage. See TLB-639.
        if (force && profile is null)
        {
            error = "install --force applies only to --profile";
            return false;
        }
        invocation = new InstallInvocation(profile, invariants, force);
        return true;
    }

    public static async Task<int> ExecuteAsync(
        InstallInvocation invocation,
        bool json,
        string cwd,
        TextReader input,
        TextWriter output,
        TextWriter diagnostics,
        CancellationToken ct,
        InstallDependencies? dependencies = null)
    {
        var deps = dependencies ?? DefaultDependencies();
        try
        {
            if (invocation.ProfilePath is null && invocation.InvariantsPath is null)
            {
                Progress(diagnostics, "stage 1/3: initializing configuration and provisioning setup");
                var prepareExit = await deps.EnsureInitAndSetup(cwd, input, diagnostics, ct).ConfigureAwait(false);
                if (prepareExit != 0)
                    return Failure(json, output, diagnostics, "install initialization/setup failed", prepareExit);

                var prompt = ProfilePromptLoader.LoadRepository();
                return Handoff(json, output, "profile_handoff", prompt,
                    "build install --profile .build/profile.json");
            }

            if (invocation.ProfilePath is not null)
            {
                Progress(diagnostics, "stage 2/3: applying repository profile");
                var apply = ApplyProfile(cwd, invocation.ProfilePath, invocation.Force, input);
                if (!apply.Success)
                    return Failure(json, output, diagnostics, apply.Message, apply.ExitCode);

                Progress(diagnostics, "stage 2/3: installing binary-hosted SOP stubs");
                var sopExit = await deps.InstallSop(cwd, diagnostics, ct).ConfigureAwait(false);
                if (sopExit != 0)
                    return Failure(json, output, diagnostics, "SOP installation failed", sopExit);
                var platform = ResolveGeneratedPlatform(cwd, apply.Framework!, apply.Language!);
                if (!platform.Success)
                    return Failure(json, output, diagnostics, platform.Message, 1);

                var prompt = ConductorPromptLoader.Load();
                return Handoff(json, output, "invariants_handoff", prompt,
                    "build install --invariants .build/invariants.toml");
            }

            Progress(diagnostics, "stage 3/3: applying review invariants");
            var conductorOut = new StringWriter();
            var conductorExit = ConductorCommand.Execute(
                ["conductor", "apply", invocation.InvariantsPath!],
                true,
                cwd,
                input,
                conductorOut,
                diagnostics);
            if (conductorExit != 0)
                return Failure(json, output, diagnostics, "review invariant application failed", conductorExit);

            Progress(diagnostics, "stage 3/3: validating profile, conductor data, and SOP stubs");
            var doctor = deps.RunDoctor(cwd);
            if (!doctor.Passed)
            {
                var findings = string.Join("; ", doctor.Findings.Select(f => $"{f.Code}: {f.Message}"));
                return Failure(json, output, diagnostics, $"SOP doctor blocked readiness: {findings}", 1);
            }

            var configPath = BuildConfigLoader.FindConfigFile(cwd);
            if (configPath is null)
                return Failure(json, output, diagnostics, "config file not found", 2);
            var config = BuildConfigLoader.Load(configPath);
            var protectedBranch = config.Ship.BaseBranch;
            var stubPaths = SopBundleCatalog.All
                .SelectMany(entry => entry.OwnedPaths)
                .Where(path => string.Equals(path.Class, SopBundleCatalog.EmittedPathClass, StringComparison.Ordinal))
                .Select(path => path.Path.Replace('\\', '/'))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            Progress(diagnostics, "stage 3/3: preparing run branch and committing exact installer-owned readiness paths");
            var readiness = await deps.AssertReadiness(
                cwd, protectedBranch, stubPaths, config, ct).ConfigureAwait(false);
            if (!readiness.Success)
                return Failure(json, output, diagnostics, readiness.Message, 1);

            var ready = new InstallView(
                "ready", true, "READY: run-backlog preflight passed", null, null,
                readiness.Branch, readiness.Porcelain, stubPaths);
            WriteResult(json, output, ready);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return Failure(json, output, diagnostics, "installation cancelled", 1);
        }
        catch (Exception ex)
        {
            return Failure(json, output, diagnostics, $"installation failed: {ex.Message}", 1);
        }
    }

    private static InstallDependencies DefaultDependencies() => new(
        EnsureInitAndSetupAsync,
        async (cwd, diagnostics, ct) =>
        {
            var ignoredOutput = new StringWriter();
            return await SopCommand.ExecuteInstallAsync(
                ["sop", "install"], true, cwd, ignoredOutput, diagnostics, ct).ConfigureAwait(false);
        },
        cwd => SopDoctorCommand.RunDoctor(cwd, BuildVersion.Current),
        (cwd, protectedBranch, stubPaths, config, ct) =>
            InstallReadiness.PrepareAndAssertAsync(
                cwd, protectedBranch, RunBranch, stubPaths,
                config.Worktree.Root, config.Worktree.SeedFiles, config.Project.InstallCommand, ct));

    private static async Task<int> EnsureInitAndSetupAsync(
        string cwd, TextReader input, TextWriter diagnostics, CancellationToken ct)
    {
        if (BuildConfigLoader.FindConfigFile(cwd) is null)
        {
            var initConsole = new InstallConsole(input, diagnostics);
            var initExit = await InitCommand.ExecuteAsync(
                cwd, false, false, initConsole, probeCodex: null, ct: ct).ConfigureAwait(false);
            if (initExit != 0)
                return initExit;
        }

        var bootstrap = await CliBootstrap.CreateAsync(cwd, ct, requireTicketing: true).ConfigureAwait(false);
        if (bootstrap.Failure is not null)
        {
            diagnostics.WriteLine($"{bootstrap.Failure.HumanPrefix}: {bootstrap.Failure.Message}");
            return bootstrap.Failure.ExitCode;
        }

        using var context = bootstrap.Context!;
        var setup = new SetupCommand(context.Ticketing, new FileSystemLocalRepoOps(context.WorkingDirectory));
        return await setup.ExecuteAsync(false, new InstallConsole(input, diagnostics), ct).ConfigureAwait(false);
    }

    private static InstallProfileApplyResult ApplyProfile(
        string cwd, string inputPath, bool force, TextReader input)
    {
        var configPath = BuildConfigLoader.FindConfigFile(cwd);
        if (configPath is null)
            return new(false, 2, "config file not found", null, null);
        string json;
        try
        {
            json = inputPath == "-"
                ? input.ReadToEnd()
                : File.ReadAllText(Path.GetFullPath(inputPath, cwd));
        }
        catch (Exception ex)
        {
            return new(false, 1, $"could not read PROJECT_PROFILE input '{inputPath}': {ex.Message}", null, null);
        }
        if (!ProjectProfileParser.TryParse(json, out var profile, out var parseError) || profile is null)
            return new(false, 2, $"PROJECT_PROFILE JSON was invalid: {parseError}", null, null);

        var platform = ResolvePlatformAuthority(profile.Framework, profile.Language);
        if (!platform.Success)
            return new(false, 2, platform.Message, null, null);

        var original = File.ReadAllText(configPath);
        var outcome = ConfigProfileWriter.Apply(original, profile, force);
        if (outcome.Changed && outcome.NewText is not null)
        {
            File.WriteAllText(configPath, outcome.NewText, new UTF8Encoding(false));
            return new(true, 0, outcome.Summary ?? "profile applied", profile.Language, profile.Framework);
        }
        // Re-running the installer against an already-installed repository must reach the same handoff
        // without rewriting config; that is stage 2's half of install idempotence. See TLB-639.
        if (outcome.AlreadyMatched)
            return new(true, 0, outcome.Summary ?? ConfigProfileWriter.AlreadyMatchedSummary,
                profile.Language, profile.Framework);
        return new(false, 1, outcome.SkipReason ?? "profile apply made no change", null, null);
    }

    internal static (bool Success, string Message) ResolveGeneratedPlatform(
        string cwd,
        string framework,
        string language)
    {
        var authority = ResolvePlatformAuthority(framework, language);
        if (!authority.Success)
            return (false, authority.Message);
        var conductorPath = SopDoctorCommand.FindConductorFile(cwd);
        if (conductorPath is null)
            return (false, "missing .build/conductor.toml after SOP install");
        var platform = authority.Platform;

        var original = File.ReadAllText(conductorPath);
        var eol = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var inConstellation = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
                inConstellation = string.Equals(trimmed, "[constellation]", StringComparison.Ordinal);
            if (!inConstellation || !trimmed.StartsWith("platform", StringComparison.Ordinal))
                continue;
            if (!string.Equals(trimmed, "platform = \"unknown\"", StringComparison.Ordinal))
                return (true, "preserved existing constellation.platform");
            var indentLength = lines[i].Length - lines[i].TrimStart().Length;
            var indent = lines[i][..indentLength];
            lines[i] = $"{indent}platform = \"{EscapeToml(platform)}\"";
            File.WriteAllText(conductorPath, string.Join(eol, lines), new UTF8Encoding(false));
            return (true, $"resolved constellation.platform as '{platform}'");
        }
        return (false, "missing constellation.platform after SOP install");
    }

    private static (bool Success, string Message, string Platform) ResolvePlatformAuthority(
        string framework,
        string language)
    {
        var platform = string.IsNullOrWhiteSpace(framework) ? language.Trim() : framework.Trim();
        return platform.Length == 0
            ? (false, "profile framework and language are both empty; cannot resolve constellation.platform", string.Empty)
            : (true, string.Empty, platform);
    }

    private static string EscapeToml(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static int Handoff(bool json, TextWriter output, string stage, string prompt, string next)
    {
        WriteResult(json, output, new InstallView(stage, false, "STOP: agent handoff required", prompt, next, null, null, []));
        return 0;
    }

    private static void WriteResult(bool json, TextWriter output, InstallView view)
    {
        if (json)
        {
            CliEnvelopeWriter.WriteInstall(output, view);
            return;
        }
        output.WriteLine($"INSTALL_STAGE={view.Stage}");
        output.WriteLine(view.Message);
        if (view.Prompt is not null)
        {
            output.WriteLine("PROMPT_BEGIN");
            output.Write(view.Prompt);
            if (!view.Prompt.EndsWith('\n')) output.WriteLine();
            output.WriteLine("PROMPT_END");
        }
        if (view.NextCommand is not null) output.WriteLine($"NEXT={view.NextCommand}");
        if (view.Branch is not null) output.WriteLine($"BRANCH={view.Branch}");
        output.WriteLine(view.Ready ? "READY" : "STOP");
    }

    private static int Failure(bool json, TextWriter output, TextWriter diagnostics, string message, int exitCode)
    {
        if (json) CliEnvelopeWriter.WriteError(output, CliErrorCodes.Failure, message);
        else diagnostics.WriteLine($"Error: {message}");
        return exitCode;
    }

    private static void Progress(TextWriter diagnostics, string message) =>
        diagnostics.WriteLine($"[install] {message}");

    private sealed class InstallConsole(TextReader input, TextWriter diagnostics) : IConsole
    {
        public bool IsInputRedirected => false;
        public string? ReadLine() => input.ReadLine();
        public char? ReadKeyChar()
        {
            var value = input.Read();
            return value < 0 ? null : (char)value;
        }
        public void Write(string value) => diagnostics.Write(value);
        public void WriteLine(string value) => diagnostics.WriteLine(value);
        public void ErrorWriteLine(string value) => diagnostics.WriteLine(value);
    }
}

internal sealed record InstallReadinessResult(bool Success, string Branch, string Porcelain, string Message);

internal static class InstallReadiness
{
    public static async Task<InstallReadinessResult> PrepareAndAssertAsync(
        string repositoryRoot,
        string protectedBranch,
        string runBranch,
        IReadOnlyList<string> stubPaths,
        string worktreeRoot,
        IReadOnlyList<string> seedFiles,
        string installCommand,
        CancellationToken ct,
        Func<CancellationToken, Task>? worktreeQuery = null)
    {
        var git = new InstallGit(repositoryRoot);
        var branch = (await git.RunAsync(ct, "rev-parse", "--abbrev-ref", "HEAD")).Require("resolve branch").Trim();
        var porcelain = (await git.RunAsync(ct, "status", "--porcelain")).Require("read status");
        var expandedPorcelain = (await git.RunAsync(ct, "status", "--porcelain", "--untracked-files=all"))
            .Require("read expanded status");
        var commitPaths = stubPaths.ToList();
        if (ContainsPath(expandedPorcelain, ".gitignore"))
        {
            if (!await IsDeterministicSetupGitignoreAsync(git, repositoryRoot, ct).ConfigureAwait(false))
                return Fail(branch, porcelain,
                    ".gitignore differs from the deterministic build setup merge over HEAD");
            commitPaths.Add(".gitignore");
        }
        if (HasUnrelatedChanges(expandedPorcelain, commitPaths))
            return Fail(branch, porcelain, "working tree contains changes outside emitted SOP stubs");

        var gitDir = (await git.RunAsync(ct, "rev-parse", "--git-dir")).Require("resolve git directory").Trim();
        var absoluteGitDir = Path.GetFullPath(gitDir, repositoryRoot);
        if ((await git.RunAsync(ct, "rev-parse", "-q", "--verify", "MERGE_HEAD")).ExitCode == 0)
            return Fail(branch, porcelain, "an interrupted merge is in progress");
        if (Directory.Exists(Path.Combine(absoluteGitDir, "rebase-merge")) ||
            Directory.Exists(Path.Combine(absoluteGitDir, "rebase-apply")))
            return Fail(branch, porcelain, "an interrupted rebase is in progress");

        if (string.Equals(branch, "HEAD", StringComparison.Ordinal))
            return Fail(branch, porcelain, "detached HEAD cannot host a run");
        if (string.Equals(runBranch, protectedBranch, StringComparison.Ordinal))
            return Fail(branch, porcelain, $"run branch '{runBranch}' is configured as protected");
        if (string.Equals(branch, protectedBranch, StringComparison.Ordinal))
        {
            var exists = await git.RunAsync(ct, "show-ref", "--verify", "--quiet", $"refs/heads/{runBranch}");
            var switchResult = exists.ExitCode == 0
                ? await git.RunAsync(ct, "checkout", runBranch)
                : await git.RunAsync(ct, "checkout", "-b", runBranch);
            switchResult.Require($"prepare non-protected branch '{runBranch}'");
            branch = runBranch;
        }

        var existingCommitPaths = commitPaths.Where(path => File.Exists(Path.Combine(repositoryRoot, path))).ToList();
        if (existingCommitPaths.Count > 0)
        {
            (await git.RunAsync(ct, ["add", "--", .. existingCommitPaths])).Require("stage installer-owned paths");
            var staged = await git.RunAsync(ct, ["diff", "--cached", "--quiet", "--", .. existingCommitPaths]);
            if (staged.ExitCode == 1)
                (await git.RunAsync(ct, ["commit", "--only", "-m", "build: install run-backlog readiness files", "--", .. existingCommitPaths]))
                    .Require("commit installer-owned paths");
            else if (staged.ExitCode != 0)
                staged.Require("inspect staged installer-owned paths");
        }

        branch = (await git.RunAsync(ct, "rev-parse", "--abbrev-ref", "HEAD")).Require("resolve final branch").Trim();
        porcelain = (await git.RunAsync(ct, "status", "--porcelain")).Require("read final status");
        if (porcelain.Length != 0)
            return Fail(branch, porcelain, "working tree is not clean after stub commit");
        if (string.Equals(branch, protectedBranch, StringComparison.Ordinal) || string.Equals(branch, "HEAD", StringComparison.Ordinal))
            return Fail(branch, porcelain, $"branch '{branch}' is protected or detached");
        if ((await git.RunAsync(ct, "rev-parse", "-q", "--verify", "MERGE_HEAD")).ExitCode == 0)
            return Fail(branch, porcelain, "an interrupted merge is in progress");
        if (Directory.Exists(Path.Combine(absoluteGitDir, "rebase-merge")) ||
            Directory.Exists(Path.Combine(absoluteGitDir, "rebase-apply")))
            return Fail(branch, porcelain, "an interrupted rebase is in progress");

        try
        {
            if (worktreeQuery is not null)
                await worktreeQuery(ct).ConfigureAwait(false);
            else
            {
                var root = Path.IsPathRooted(worktreeRoot)
                    ? Path.GetFullPath(worktreeRoot)
                    : Path.GetFullPath(Path.Combine(repositoryRoot, worktreeRoot));
                var manager = new WorktreeLeaseManager(
                    new ProcessGitClient(repositoryRoot),
                    new ProcessInstallCommandRunner(),
                    new WorktreeLeaseOptions(repositoryRoot, repositoryRoot, root,
                        seedFiles, installCommand));
                _ = await manager.ListAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(branch, porcelain, $"worktree list query failed: {ex.Message}");
        }

        return new InstallReadinessResult(true, branch, porcelain, "run-backlog preflight passed");
    }

    private static bool HasUnrelatedChanges(string porcelain, IReadOnlyList<string> allowedPaths)
    {
        var allowed = new HashSet<string>(allowedPaths.Select(path => path.Replace('\\', '/')), StringComparer.Ordinal);
        foreach (var line in porcelain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) return true;
            var path = line[3..].Replace('\\', '/');
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            if (!allowed.Contains(path)) return true;
        }
        return false;
    }

    private static bool ContainsPath(string porcelain, string expectedPath)
    {
        foreach (var line in porcelain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var path = line[3..].Replace('\\', '/');
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            if (string.Equals(path, expectedPath, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static async Task<bool> IsDeterministicSetupGitignoreAsync(
        InstallGit git,
        string repositoryRoot,
        CancellationToken ct)
    {
        var head = await git.RunAsync(ct, "show", "HEAD:.gitignore").ConfigureAwait(false);
        var headText = head.ExitCode == 0 ? head.Stdout : null;
        var expected = GitignoreManager.Merge(headText);
        if (expected is null)
            return false;
        var path = Path.Combine(repositoryRoot, ".gitignore");
        return File.Exists(path) && string.Equals(File.ReadAllText(path), expected, StringComparison.Ordinal);
    }

    private static InstallReadinessResult Fail(string branch, string porcelain, string reason) =>
        new(false, branch, porcelain,
            $"readiness blocked: {reason}; branch={branch}; git status --porcelain:\n{porcelain}");
}

internal sealed record InstallGitResult(int ExitCode, string Stdout, string Stderr)
{
    public string Require(string operation)
    {
        if (ExitCode != 0) throw new InvalidOperationException($"could not {operation}: git exited {ExitCode}: {Stderr.Trim()}");
        return Stdout;
    }

}

internal sealed class InstallGit(string repositoryRoot)
{
    public Task<InstallGitResult> RunAsync(CancellationToken ct, params string[] args) => RunAsync(ct, (IReadOnlyList<string>)args);

    public async Task<InstallGitResult> RunAsync(CancellationToken ct, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not be started");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new InstallGitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
