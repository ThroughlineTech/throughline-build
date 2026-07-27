using System.Diagnostics;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ImplementCliTests
{
    [Fact]
    public void UsageText_ContainsImplementVerb()
    {
        Assert.Contains("implement", CliUsage.UsageText);
        Assert.Contains("build implement <ticket-id>", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_ListsAllExitCodes()
    {
        Assert.Contains("0  Success", CliUsage.UsageText);
        Assert.Contains("1  Phase or command failure", CliUsage.UsageText);
        Assert.Contains("2  Config error or unknown verb", CliUsage.UsageText);
        Assert.Contains("3  Missing secret", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsImplementOnSameStyleAsPlan()
    {
        // Both verbs take a ticket-id and no other required flags.
        var planLine = "build plan <ticket-id>";
        var implementLine = "build implement <ticket-id>";
        Assert.Contains(planLine, CliUsage.UsageText);
        Assert.Contains(implementLine, CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsQuietFlag()
    {
        // --quiet must appear in both the verb usage and the Flags block.
        Assert.Contains("--quiet", CliUsage.UsageText);
        Assert.Contains("--debug|--quiet", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_MentionsSummaryJsonFlag()
    {
        Assert.Contains("--summary-json", CliUsage.UsageText);
    }

    [Fact]
    public void ImplementPhase_AcceptsSameDependencyShapeAsPlanPhase()
    {
        // Both phases must construct from the same shape so the CLI's
        // ticketing / worker / sink / options bundle works for either path.
        var ticketing = new StubTicketing();
        var worker = new StubWorker();
        var events = new StubSink();
        var options = new BuildOptions("sid", "claude-code", TimeSpan.FromMinutes(5));

        var planPhase = new PlanPhase(ticketing, worker, events, options);
        var implementPhase = new ImplementPhase(ticketing, worker, events, options);

        Assert.Equal(Phase.Plan, planPhase.Phase);
        Assert.Equal(Phase.Implement, implementPhase.Phase);
    }

    [Fact]
    public async Task BuildBinary_NoArgs_PrintsUsageAndExitsZero()
    {
        var exe = LocateBuildExecutable();
        if (exe is null)
        {
            // Skip on CI shapes that haven't published the binary yet.
            return;
        }

        var (exitCode, stdout, _) = await RunProcess(exe, Array.Empty<string>());

        Assert.Equal(0, exitCode);
        Assert.Contains("implement", stdout);
        Assert.Contains("Usage:", stdout);
    }

    [Fact]
    public async Task BuildBinary_HelpFlag_OutputContainsImplementVerb()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, _) = await RunProcess(exe, new[] { "--help" });

        Assert.Equal(0, exitCode);
        Assert.Contains("build implement <ticket-id>", stdout);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-V")]
    public async Task BuildBinary_VersionFlag_PrintsSingleVersionLineAndExitsZero(string flag)
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, stderr) = await RunProcess(exe, new[] { flag });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var normalized = stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.EndsWith("\n", normalized, StringComparison.Ordinal);

        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal(BuildVersion.Current, lines[0]);
    }

    [Fact]
    public async Task BuildBinary_ImplementWithoutTicketId_ExitsTwo()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, _, stderr) = await RunProcess(exe, new[] { "implement" });

        Assert.Equal(2, exitCode);
        Assert.Contains("ticket-id is required", stderr);
        Assert.Contains("build implement <ticket-id>", stderr);
    }

    [Fact]
    public async Task BuildBinary_InvokedFromSubdirectory_DoesNotLeaveEmptyEventFiles()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var mainRepo = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            // Create a minimal main-repo layout with .build/config.toml
            var buildDir = Path.Combine(mainRepo, ".build");
            Directory.CreateDirectory(buildDir);

            var configToml =
                "[ticketing]\n" +
                "backend = \"plane\"\n" +
                "plane_base_url = \"https://stub.plane.invalid\"\n" +
                "plane_workspace_slug = \"stub-ws\"\n" +
                "plane_project_id = \"00000000-0000-0000-0000-000000000000\"\n" +
                "\n" +
                "[workers]\n" +
                "default_agent = \"claude-code\"\n" +
                "\n" +
                "[workers.claude-code]\n" +
                "executable = \"claude\"\n" +
                "\n" +
                "[workers.claude-code.sizes]\n" +
                "small = { model = \"claude-haiku-4-5-20251001\" }\n" +
                "medium = { model = \"claude-sonnet-4-6\" }\n" +
                "large = { model = \"claude-opus-4-7\" }\n" +
                "\n" +
                "[events]\n" +
                "log_directory = \".build/events\"\n";

            File.WriteAllText(Path.Combine(buildDir, "config.toml"), configToml);

            // Create the simulated worktree sub-directory
            var subDir = Path.Combine(mainRepo, "sub");
            Directory.CreateDirectory(subDir);

            // Invoke build implement from the sub-directory; tolerate any non-zero exit
            var env = new Dictionary<string, string> { { "PLANE_API_TOKEN", "stub" } };
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = subDir
            };
            psi.ArgumentList.Add("implement");
            psi.ArgumentList.Add("TLB-bogus");
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            await stdoutTask;
            await stderrTask;

            // Assert: the sub-directory does NOT contain .build/events/
            var eventsInSub = Path.Combine(subDir, ".build", "events");
            Assert.False(Directory.Exists(eventsInSub),
                $"Expected no events directory in sub-worktree, but found: {eventsInSub}");
        }
        finally
        {
            if (Directory.Exists(mainRepo))
                Directory.Delete(mainRepo, recursive: true);
        }
    }

    [Fact]
    public async Task BuildBinary_InvokedFromRealGitWorktree_DoesNotLeaveEventsInWorktree()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var tempBaseDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var mainRepo = Path.Combine(tempBaseDir, "main-repo");
        var worktreeDir = Path.Combine(tempBaseDir, "worktree");

        try
        {
            // Initialize main repo as a git repository with an initial commit
            Directory.CreateDirectory(mainRepo);
            await RunGit(mainRepo, "init");
            await RunGit(mainRepo, "config", "user.email", "test@example.com");
            await RunGit(mainRepo, "config", "user.name", "Test User");

            // Create and commit a dummy file so main branch exists
            var dummyFile = Path.Combine(mainRepo, "README.md");
            File.WriteAllText(dummyFile, "# Test Repo\n");
            await RunGit(mainRepo, "add", "README.md");
            await RunGit(mainRepo, "commit", "-m", "initial commit");

            // Create .build/config.toml in main repo
            var buildDir = Path.Combine(mainRepo, ".build");
            Directory.CreateDirectory(buildDir);
            var configToml =
                "[ticketing]\n" +
                "backend = \"plane\"\n" +
                "plane_base_url = \"https://stub.plane.invalid\"\n" +
                "plane_workspace_slug = \"stub-ws\"\n" +
                "plane_project_id = \"00000000-0000-0000-0000-000000000000\"\n" +
                "\n" +
                "[workers]\n" +
                "default_agent = \"claude-code\"\n" +
                "\n" +
                "[workers.claude-code]\n" +
                "executable = \"claude\"\n" +
                "\n" +
                "[workers.claude-code.sizes]\n" +
                "small = { model = \"claude-haiku-4-5-20251001\" }\n" +
                "medium = { model = \"claude-sonnet-4-6\" }\n" +
                "large = { model = \"claude-opus-4-7\" }\n" +
                "\n" +
                "[events]\n" +
                "log_directory = \".build/events\"\n";
            File.WriteAllText(Path.Combine(buildDir, "config.toml"), configToml);
            await RunGit(mainRepo, "add", ".build/config.toml");
            await RunGit(mainRepo, "commit", "-m", "add config");

            // Create a git worktree pointing to a new branch
            await RunGit(mainRepo, "worktree", "add", worktreeDir, "-b", "feature-branch");

            // Invoke build implement from the worktree directory
            var env = new Dictionary<string, string> { { "PLANE_API_TOKEN", "stub" } };
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = worktreeDir
            };
            psi.ArgumentList.Add("implement");
            psi.ArgumentList.Add("TLB-bogus");
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            await stdoutTask;
            await stderrTask;

            // Assert: the worktree directory does NOT contain .build/events
            var eventsInWorktree = Path.Combine(worktreeDir, ".build", "events");
            Assert.False(Directory.Exists(eventsInWorktree),
                $"Expected no events directory in worktree, but found: {eventsInWorktree}");

            // The events should only be in the main repo
            var eventsInMainRepo = Path.Combine(mainRepo, ".build", "events");
            // Note: we don't assert that events exist in main repo since the implement
            // might fail due to invalid ticket ID, but the point is it shouldn't create
            // them in the worktree
        }
        finally
        {
            // Clean up: remove the worktree first, then force delete remaining files
            try
            {
                if (Directory.Exists(mainRepo))
                {
                    try
                    {
                        await RunGit(mainRepo, "worktree", "remove", "--force", worktreeDir);
                    }
                    catch
                    {
                        // Ignore git errors; we'll clean up manually
                    }
                }
            }
            catch
            {
                // Ignore any errors during git cleanup
            }

            // Force delete any remaining files
            if (Directory.Exists(tempBaseDir))
            {
                try
                {
                    // Try up to 3 times with small waits for file locks to release
                    for (int retry = 0; retry < 3; retry++)
                    {
                        try
                        {
                            Directory.Delete(tempBaseDir, recursive: true);
                            break;
                        }
                        catch when (retry < 2)
                        {
                            await Task.Delay(100);
                        }
                    }
                }
                catch
                {
                    // If cleanup fails after retries, log but don't fail the test
                    // (the OS will clean up eventually)
                }
            }
        }
    }

    private static string? LocateBuildExecutable()
    {
        var here = AppContext.BaseDirectory;
        // Walk up from the test assembly to the repo root.
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "throughline-build.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        var config = here.Contains(Path.Combine("bin", "Release"), StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var binDir = Path.Combine(dir.FullName, "src", "ThroughlineBuild.Cli", "bin", config, "net8.0");
        var exeName = OperatingSystem.IsWindows() ? "build.exe" : "build";
        var fullPath = Path.Combine(binDir, exeName);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcess(string exe, string[] args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed with exit code {proc.ExitCode}: {stderr}");
    }

    private sealed class StubTicketing : ITicketing
    {
        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames, CancellationToken ct) => throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;

        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
                string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
                Task.FromResult(new CreateChildTicketsResult(
                    children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                    Array.Empty<string>()));
    }

    private sealed class StubWorker : IWorkerAgent
    {
        public string Name => "stub";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class StubSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
